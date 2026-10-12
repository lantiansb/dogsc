using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>Mac EditorSliderValueFormat: how a parameter is read out and typed.</summary>
internal enum NumFormat
{
    Plain,
    /// <summary>Rounded integer without unit.</summary>
    Pixels,
    /// <summary>0...1 ratio shown as a rounded percentage.</summary>
    Percent,
    Multiplier,
    Seconds,
    Degrees,
    /// <summary>Degrees with one decimal (tilt readouts).</summary>
    DegreesDecimal,
    Decimal1,
    Decimal2,
    Milliseconds
}

internal static class NumFormatting
{
    private static string Trim(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    public static string Text(NumFormat format, double value)
    {
        if (!double.IsFinite(value)) return "–";
        return format switch
        {
            NumFormat.Pixels => Math.Round(value).ToString("0", CultureInfo.CurrentCulture),
            NumFormat.Percent => Math.Round(value * 100).ToString("0", CultureInfo.CurrentCulture) + "%",
            NumFormat.Multiplier => Trim(value) + "×",
            NumFormat.Seconds => Trim(value) + "s",
            NumFormat.Degrees => Math.Round(value).ToString("0", CultureInfo.CurrentCulture) + "°",
            NumFormat.DegreesDecimal => value.ToString("0.0", CultureInfo.CurrentCulture) + "°",
            NumFormat.Decimal1 => value.ToString("0.0", CultureInfo.CurrentCulture),
            NumFormat.Decimal2 => value.ToString("0.00", CultureInfo.CurrentCulture),
            NumFormat.Milliseconds => Math.Round(value).ToString("0", CultureInfo.CurrentCulture) + " ms",
            _ => Trim(value)
        };
    }

    /// <summary>Unit-free text shown while the readout is being edited.</summary>
    public static string EditText(NumFormat format, double value)
    {
        if (!double.IsFinite(value)) return "";
        return format switch
        {
            NumFormat.Pixels or NumFormat.Degrees or NumFormat.Milliseconds => Math.Round(value).ToString("0", CultureInfo.CurrentCulture),
            NumFormat.Percent => Math.Round(value * 100).ToString("0", CultureInfo.CurrentCulture),
            NumFormat.Decimal1 or NumFormat.DegreesDecimal => value.ToString("0.0", CultureInfo.CurrentCulture),
            _ => Trim(value)
        };
    }

    /// <summary>Parses what <see cref="Text"/> shows, with or without its unit.</summary>
    public static bool TryParse(NumFormat format, string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var raw = text.Trim();
        foreach (var unit in new[] { "ms", "px", "%", "s", "×", "x", "X", "°" })
            if (raw.EndsWith(unit, StringComparison.Ordinal)) { raw = raw[..^unit.Length].Trim(); break; }
        if (raw.Length == 0) return false;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (!double.IsFinite(value)) return false;
        if (format == NumFormat.Percent) value /= 100;
        return true;
    }
}

/// <summary>
/// Physical slider travel. Mac EditorSliderScale: multiplier / seconds / points parameters use a
/// logarithmic "knee" curve so the useful low range is not squeezed into the first pixels.
/// </summary>
internal readonly struct SliderScale(bool logarithmic, double knee)
{
    public bool Logarithmic { get; } = logarithmic;
    private readonly double knee = Math.Max(knee, 1e-6);

    public static SliderScale Linear => new(false, 1);

    public static SliderScale For(NumFormat format, double min, double max)
    {
        if (min < 0) return Linear;
        return format switch
        {
            NumFormat.Multiplier => new(true, 1),
            NumFormat.Seconds => new(true, .5),
            NumFormat.Pixels => new(true, Math.Max((max - min) / 8, 1)),
            _ => Linear
        };
    }

    public double Fraction(double value, double min, double max)
    {
        var span = max - min;
        if (span <= 0 || !double.IsFinite(value)) return 0;
        var delta = Math.Clamp(value - min, 0, span);
        return Logarithmic ? Math.Log(1 + delta / knee) / Math.Log(1 + span / knee) : delta / span;
    }

    public double Value(double fraction, double min, double max)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var span = max - min;
        if (span <= 0) return min;
        return Logarithmic
            ? Math.Min(min + knee * (Math.Exp(fraction * Math.Log(1 + span / knee)) - 1), max)
            : min + fraction * span;
    }
}

/// <summary>
/// Editable numeric readout. Valid text previews immediately, Enter commits, Esc cancels,
/// invalid text turns the box red and is never committed (Mac EditorInspectorParameterReadout).
/// </summary>
internal sealed class InspectorReadout
{
    private readonly TextBox box;
    private readonly Func<string, double?> parse;
    private readonly Func<double, string> display;
    private readonly Func<double, string> editText;
    private readonly Func<double> committedValue;
    private readonly Action<double> preview;
    private readonly Action<double> commit;
    private readonly Action cancel;
    private bool programmatic;
    private bool editing;
    private bool invalid;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, InspectorReadout> Registry = new();

    /// <summary>True when the focused control is a numeric readout with typed, uncommitted text (the readout owns Esc then).</summary>
    public static bool IsEditingFocused() => Keyboard.FocusedElement is TextBox focused && Registry.TryGetValue(focused, out var readout) && readout.editing;

    public InspectorReadout(TextBox box, Func<string, double?> parse, Func<double, string> display, Func<double, string> editText,
        Func<double> committedValue, Action<double> preview, Action<double> commit, Action cancel)
    {
        this.box = box;
        this.parse = parse;
        this.display = display;
        this.editText = editText;
        this.committedValue = committedValue;
        this.preview = preview;
        this.commit = commit;
        this.cancel = cancel;
        Registry.AddOrUpdate(box, this);
        SetText(display(committedValue()));
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            box.Focus();
            e.Handled = true;
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            SetText(editText(committedValue()));
            box.SelectAll();
        };
        box.TextChanged += (_, _) => OnTextChanged();
        box.PreviewKeyDown += OnKeyDown;
        box.LostKeyboardFocus += (_, _) => OnLostFocus();
    }

    public bool Editing => editing;

    /// <summary>Programmatic text change (slider moved); never counts as typing.</summary>
    public void SetText(string text)
    {
        programmatic = true;
        try { box.Text = text; }
        finally { programmatic = false; }
        MarkInvalid(false);
    }

    private void MarkInvalid(bool value)
    {
        invalid = value;
        if (value)
        {
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(216, 64, 64));
            box.ToolTip = "请输入有效数值";
        }
        else
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.ClearValue(FrameworkElement.ToolTipProperty);
        }
    }

    private void OnTextChanged()
    {
        if (programmatic) return;
        editing = true;
        if (parse(box.Text) is not { } value)
        {
            MarkInvalid(true);
            return;
        }
        MarkInvalid(false);
        preview(value);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            editing = false;
            SetText(display(committedValue()));
            cancel();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            if (!editing)
            {
                cancel();
                return;
            }
            if (parse(box.Text) is not { } value)
            {
                MarkInvalid(true);
                return;
            }
            editing = false;
            commit(value);
        }
    }

    private void OnLostFocus()
    {
        if (!editing)
        {
            SetText(display(committedValue()));
            return;
        }
        editing = false;
        if (parse(box.Text) is { } value && !invalid) commit(value);
        else
        {
            SetText(display(committedValue()));
            cancel();
        }
    }
}

public partial class MainWindow
{
    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>
    /// Slider + editable readout. Dragging, arrow keys and typing all preview live and commit one
    /// undo step; Esc restores the committed value. Mac: EditorTransactionalSliderRow.
    /// </summary>
    private void Number(string label, double value, double min, double max, Action<double> apply, NumFormat format,
        bool? log = null, bool inline = false, string? undo = null)
    {
        var scale = log switch
        {
            true => new SliderScale(true, format == NumFormat.Seconds ? .5 : 1),
            false => SliderScale.Linear,
            _ => SliderScale.For(format, min, max)
        };
        NumberCore(label, value, min, max, apply, v => NumFormatting.Text(format, v), v => NumFormatting.EditText(format, v),
            text => NumFormatting.TryParse(format, text, out var parsed) ? parsed : null, scale, inline, undo);
    }

    /// <summary>Legacy overload: caller supplies the unit suffix and a .NET number format.</summary>
    private void Number(string label, double value, double min, double max, Action<double> apply, string suffix = "",
        string format = "0.##", bool inline = false)
    {
        string Text(double v) => v.ToString(format, CultureInfo.CurrentCulture) + suffix;
        double? Parse(string text)
        {
            var raw = suffix.Length > 0 ? text.Replace(suffix.Trim(), "").Trim() : text.Trim();
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && double.IsFinite(v)) return v;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v)) return v;
            return null;
        }
        NumberCore(label, value, min, max, apply, Text, v => v.ToString(format, CultureInfo.CurrentCulture), Parse, SliderScale.Linear, inline, null);
    }

    private void NumberCore(string label, double value, double min, double max, Action<double> apply,
        Func<double, string> text, Func<double, string> editText, Func<string, double?> parse,
        SliderScale scale, bool inline, string? undo)
    {
        var gesture = new InspectorGesture(this, undo ?? "调整" + label);
        var committed = value;
        var suppress = false;

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
        var row = new DockPanel();
        var box = new TextBox
        {
            Width = 64, FontSize = 11, TextAlignment = TextAlignment.Center, Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0), Background = Brush("ControlSurface")
        };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        var title = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("Secondary") };
        row.Children.Add(title);
        panel.Children.Add(row);

        var slider = new InspectorSlider
        {
            Style = (Style)FindResource(typeof(Slider)),
            IsMoveToPointEnabled = true,
            Margin = new Thickness(4, 8, 4, 0)
        };
        if (scale.Logarithmic)
        {
            slider.Minimum = 0;
            slider.Maximum = 1;
            slider.ToParameter = f => scale.Value(f, min, max);
            slider.FromParameter = v => scale.Fraction(v, min, max);
        }
        else
        {
            slider.Minimum = min;
            slider.Maximum = max;
        }
        // Mac EditorSliderKeyboardBridge: arrows move 1% of the travel, Shift 10%.
        slider.SmallChange = (slider.Maximum - slider.Minimum) / 100;
        slider.LargeChange = (slider.Maximum - slider.Minimum) / 10;

        double Real(double position) => Math.Clamp(scale.Logarithmic ? scale.Value(position, min, max) : position, min, max);
        double Position(double real) => scale.Logarithmic ? scale.Fraction(real, min, max) : Math.Clamp(real, min, max);
        void SetSlider(double real)
        {
            suppress = true;
            try { slider.Value = Position(real); }
            finally { suppress = false; }
        }
        SetSlider(value);

        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        System.Windows.Automation.AutomationProperties.SetName(box, label + "数值");
        RegisterInspectorFocus(slider, label);
        RegisterInspectorFocus(box, label + "数值");

        var keyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        InspectorReadout? readout = null;

        void Finish(double real, bool releaseFocus)
        {
            keyTimer.Stop();
            real = Math.Clamp(real, min, max);
            if (Math.Abs(real - committed) < 1e-8)
            {
                gesture.Cancel();
                SetSlider(committed);
                readout!.SetText(text(committed));
                if (releaseFocus) FocusEditor();
                return;
            }
            committed = real;
            readout!.SetText(text(real));
            if (releaseFocus) CommitAndReleaseFocus(() => gesture.Commit(() => apply(real)));
            else gesture.Commit(() => apply(real));
        }

        void CancelAll()
        {
            keyTimer.Stop();
            gesture.Cancel();
            SetSlider(committed);
            readout!.SetText(text(committed));
            if (FindVisualChild<Thumb>(slider) is { IsDragging: true } thumb) thumb.CancelDrag();
        }

        readout = new InspectorReadout(box,
            raw => parse(raw) is { } parsed ? Math.Clamp(parsed, min, max) : null,
            text, editText, () => committed,
            real =>
            {
                SetSlider(real);
                gesture.Preview(() => apply(real));
            },
            real => Finish(real, true),
            () =>
            {
                CancelAll();
                FocusEditor();
            });

        slider.ValueChanged += (_, _) =>
        {
            if (suppress) return;
            var real = Real(slider.Value);
            readout.SetText(text(real));
            if (buildingInspector) return;
            gesture.Preview(() => apply(real));
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (gesture.Active) Finish(Real(slider.Value), true);
            else FocusEditor();
        };
        slider.AutomationValueCommitted += (_, _) => Finish(Real(slider.Value), false);
        slider.LostKeyboardFocus += (_, _) =>
        {
            keyTimer.Stop();
            if (gesture.Active) Finish(Real(slider.Value), false);
        };
        keyTimer.Tick += (_, _) =>
        {
            keyTimer.Stop();
            if (gesture.Active) Finish(Real(slider.Value), false);
        };
        slider.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (!gesture.Active) return;
                CancelAll();
                e.Handled = true;
                return;
            }
            var direction = e.Key is Key.Right or Key.Up ? 1 : e.Key is Key.Left or Key.Down ? -1 : 0;
            if (direction == 0 || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
            var step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? slider.LargeChange : slider.SmallChange) * direction;
            slider.Value = Math.Clamp(slider.Value + step, slider.Minimum, slider.Maximum);
            keyTimer.Stop();
            keyTimer.Start();
            e.Handled = true;
        };
        slider.KeyUp += (_, _) =>
        {
            if (!gesture.Active) return;
            keyTimer.Stop();
            keyTimer.Start();
        };

        if (inline)
        {
            title.Width = 96;
            slider.Margin = new Thickness(8, 0, 18, 0);
            row.Children.Add(slider);
        }
        else panel.Children.Add(slider);
        InspectorHost.Children.Add(panel);
    }
}
