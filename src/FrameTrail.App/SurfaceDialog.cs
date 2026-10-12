using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace FrameTrail.App;

/// <summary>App-owned messages share the same shell and keyboard rules as input dialogs.</summary>
internal static class SurfaceDialog
{
    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        string? acceptLabel = null, string? cancelLabel = null, bool preferCancel = false, string? noLabel = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var result = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel
        };
        var window = Create(owner, message, title, buttons, value => result = value,
            acceptLabel, cancelLabel, preferCancel, noLabel);
        window.ShowDialog();
        return result;
    }

    internal static Window Create(Window? owner, string message, string title,
        MessageBoxButton buttons, Action<MessageBoxResult> completed,
        string? acceptLabel = null, string? cancelLabel = null, bool preferCancel = false, string? noLabel = null)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var window = new Window
        {
            Title = title, Width = buttons == MessageBoxButton.YesNoCancel && (acceptLabel is not null || noLabel is not null) ? 580 : 480, MinWidth = 360,
            SizeToContent = SizeToContent.Height, ShowInTaskbar = false
        };
        SurfaceWindow.Configure(window, owner);
        var content = new DockPanel { Margin = new Thickness(24, 12, 24, 22) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        AutomationProperties.SetName(text, message);
        content.Children.Add(new ScrollViewer
        {
            Content = text, MaxHeight = Math.Max(100, Math.Min(520, SurfaceWindow.WorkArea(owner ?? window).Height - 200)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        Button AddButton(string label, MessageBoxResult value, bool primary, bool cancel)
        {
            var isDefault = buttons switch
            {
                MessageBoxButton.OK => value == MessageBoxResult.OK,
                MessageBoxButton.OKCancel => preferCancel ? cancel : value == MessageBoxResult.OK,
                MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => value == MessageBoxResult.No,
                _ => cancel
            };
            var button = new Button
            {
                Content = label, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 10, 16, 10), IsCancel = cancel,
                IsDefault = isDefault
            };
            if (primary) button.Style = (owner ?? window).TryFindResource("PrimaryButton") as Style;
            AutomationProperties.SetAutomationId(button, "Dialog" + value);
            button.Click += (_, _) => { completed(value); window.Close(); };
            actions.Children.Add(button);
            return button;
        }

        Button initialFocus;
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                initialFocus = AddButton(noLabel ?? "否", MessageBoxResult.No, false, true);
                AddButton(acceptLabel ?? "是", MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.YesNoCancel:
                initialFocus = AddButton(cancelLabel ?? "取消", MessageBoxResult.Cancel, false, true);
                AddButton(noLabel ?? "否", MessageBoxResult.No, false, false);
                AddButton(acceptLabel ?? "是", MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.OKCancel:
                var cancelButton = AddButton(cancelLabel ?? "取消", MessageBoxResult.Cancel, false, true);
                var acceptButton = AddButton(acceptLabel ?? "确定", MessageBoxResult.OK, true, false);
                initialFocus = preferCancel ? cancelButton : acceptButton;
                break;
            default:
                initialFocus = AddButton("确定", MessageBoxResult.OK, true, false);
                break;
        }
        window.Content = SurfaceWindow.WrapContent(window, content, title);
        window.ContentRendered += (_, _) => Keyboard.Focus(initialFocus);
        return window;
    }
}
