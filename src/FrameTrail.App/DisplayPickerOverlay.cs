using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameTrail.Capture;

namespace FrameTrail.App;

/// <summary>
/// Mac CaptureDisplaySelection (v0.1.4): one overlay per display; click anywhere selects it, the island button
/// switches in place between "选择此显示器" and "● 开始录制 Ctrl+R"; a single display is selected immediately;
/// plugging/unplugging a display refreshes the overlays and cancels when the selected display disappeared.
/// </summary>
internal sealed class DisplayPickerSession
{
    private readonly Dictionary<string, DisplayPickerWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Task<IReadOnlyList<DisplayInfo>>> load;
    private readonly Action<string>? notify;
    private readonly TaskCompletionSource<DisplayInfo?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<DisplayInfo> displays = [];
    private PickerKeyboard? keys;
    private string? selectedId;
    private bool finished, refreshing, refreshAgain;

    private DisplayPickerSession(Func<Task<IReadOnlyList<DisplayInfo>>> load, Action<string>? notify)
    {
        this.load = load;
        this.notify = notify;
    }

    /// <summary>Returns the display the user chose to record, or null when cancelled.</summary>
    internal static Task<DisplayInfo?> PickAsync(Func<Task<IReadOnlyList<DisplayInfo>>> load, Action<string>? notify = null) =>
        new DisplayPickerSession(load, notify).RunAsync();

    private async Task<DisplayInfo?> RunAsync()
    {
        displays = await load();
        if (displays.Count == 0) throw new InvalidOperationException("没有可录制的显示器。");
        if (displays.Count == 1) selectedId = displays[0].Id;
        try
        {
            SyncWindows();
            keys = new PickerKeyboard(() => Finish(null), ConfirmSelected);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            return await done.Task;
        }
        finally
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            keys?.Dispose();
            foreach (var window in windows.Values) window.Close();
            windows.Clear();
        }
    }

    private void Finish(DisplayInfo? result)
    {
        if (finished) return;
        finished = true;
        done.TrySetResult(result);
    }

    private void ConfirmSelected()
    {
        if (finished || selectedId is null) return;
        Finish(displays.FirstOrDefault(d => SameId(d.Id, selectedId)));
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Select(string id)
    {
        if (finished || selectedId is not null && SameId(selectedId, id)) return;
        selectedId = id;
        foreach (var window in windows.Values) window.SetSelected(SameId(window.DisplayId, id));
    }

    // SystemEvents raises on its own thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));

    private async Task RefreshAsync()
    {
        if (finished) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                IReadOnlyList<DisplayInfo> latest;
                try { latest = await load(); }
                catch (Exception error) { App.Log(error); return; }
                if (finished) return;
                if (latest.Count == 0) { Finish(null); return; }
                displays = latest;
                // Never silently switch to another display: a missing selection cancels the whole selection.
                if (selectedId is not null && !displays.Any(d => SameId(d.Id, selectedId)))
                {
                    notify?.Invoke("所选显示器已断开，已取消选择。");
                    Finish(null);
                    return;
                }
                if (displays.Count == 1) selectedId = displays[0].Id;
                SyncWindows();
            } while (refreshAgain && !finished);
        }
        finally { refreshing = false; }
    }

    private void SyncWindows()
    {
        foreach (var id in windows.Keys.Where(id => !displays.Any(d => SameId(d.Id, id))).ToArray())
        {
            windows[id].Close();
            windows.Remove(id);
        }
        for (var index = 0; index < displays.Count; index++)
        {
            var display = displays[index];
            if (windows.TryGetValue(display.Id, out var existing))
            {
                existing.UpdateDisplay(display, DisplayNumber(display, index));
                existing.SetSelected(selectedId is not null && SameId(selectedId, display.Id));
                continue;
            }
            var window = new DisplayPickerWindow(display, DisplayNumber(display, index));
            window.SelectRequested += Select;
            window.StartRequested += ConfirmSelected;
            window.SetSelected(selectedId is not null && SameId(selectedId, display.Id));
            windows[display.Id] = window;
            window.Show();
        }
    }

    /// <summary>"\\.\DISPLAY2" → 2, so the number matches Windows' own display numbering.</summary>
    private static int DisplayNumber(DisplayInfo display, int index)
    {
        var match = Regex.Match(display.Id, @"DISPLAY(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : index + 1;
    }
}

internal sealed class DisplayPickerWindow : PickerOverlayWindow
{
    private readonly Border dim = new();
    private readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = PickerStyle.White, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock detail = new() { FontSize = 12.5, Foreground = PickerStyle.WhiteMuted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    private readonly PickerChip button = new("选择此显示器", PickerChip.Look.Plain, 210, 40) { IsFilled = true };
    private bool selected;

    internal string DisplayId { get; private set; }

    internal event Action<string>? SelectRequested;
    internal event Action? StartRequested;

    internal DisplayPickerWindow(DisplayInfo display, int number) : base(display.Bounds, noActivate: true)
    {
        DisplayId = display.Id;
        Title = "DogSC · 选择显示器";
        var root = new Grid();
        root.Children.Add(dim);

        var island = new Border
        {
            Background = PickerStyle.Glass,
            BorderBrush = PickerStyle.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(30),
            Padding = new Thickness(26, 20, 26, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 280
        };
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(detail);
        button.Margin = new Thickness(0, 14, 0, 0);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.Click += () => { if (selected) StartRequested?.Invoke(); else SelectRequested?.Invoke(DisplayId); };
        stack.Children.Add(button);
        island.Child = stack;
        root.Children.Add(island);
        Content = root;

        // Clicking anywhere on an unselected display selects it (and does not press the island button).
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (selected) return;
            e.Handled = true;
            SelectRequested?.Invoke(DisplayId);
        };
        UpdateDisplay(display, number);
        ApplySelection();
    }

    internal void UpdateDisplay(DisplayInfo display, int number)
    {
        DisplayId = display.Id;
        if (display.Bounds != ScreenRect) MoveTo(display.Bounds);
        var hz = PickerNative.GetRefreshRate(display.Id);
        title.Text = $"显示器 {number}";
        detail.Text = $"{display.Bounds.Width} × {display.Bounds.Height}" + (hz > 0 ? $" · {hz} Hz" : string.Empty)
            + (display.IsPrimary ? " · 主显示器" : string.Empty);
    }

    internal void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        ApplySelection();
    }

    private void ApplySelection()
    {
        if (selected)
        {
            dim.Background = PickerStyle.Frozen(Color.FromArgb(0x14, PickerStyle.Accent.R, PickerStyle.Accent.G, PickerStyle.Accent.B));
            dim.BorderBrush = PickerStyle.Frozen(PickerStyle.Accent);
            dim.BorderThickness = new Thickness(4);
            button.SetText("● 开始录制", "Ctrl+R");
            button.IsPrimary = true;
            Cursor = System.Windows.Input.Cursors.Arrow;
        }
        else
        {
            dim.Background = PickerStyle.Frozen(Color.FromArgb(0x73, 0, 0, 0));
            dim.BorderThickness = new Thickness(0);
            button.SetText("选择此显示器");
            button.IsPrimary = false;
            Cursor = System.Windows.Input.Cursors.Hand;
        }
    }
}
