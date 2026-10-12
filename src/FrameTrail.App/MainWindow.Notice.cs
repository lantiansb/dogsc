using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrameTrail.App;

public partial class MainWindow
{
    // Mac EditorTransientNotice: a small top-right notice that never blocks input and fades out
    // by itself. Used for expected, user-correctable conditions and for undo/redo feedback.
    private DispatcherTimer? noticeTimer;

    internal void ShowNotice(string message, bool warning = false)
    {
        if (string.IsNullOrWhiteSpace(message) || NoticeHost is null) return;
        NoticeText.Text = message;
        NoticeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, warning ? "Danger" : "Accent");
        if (warning && TryFindResource("Danger") is null) NoticeDot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x5F));
        NoticeHost.BeginAnimation(OpacityProperty, null);
        NoticeHost.Opacity = 1;
        NoticeHost.Visibility = Visibility.Visible;
        noticeTimer ??= new DispatcherTimer();
        noticeTimer.Stop();
        noticeTimer.Interval = TimeSpan.FromSeconds(warning ? 6 : Math.Clamp(1.6 + message.Length * .05, 2.2, 5));
        noticeTimer.Tick -= NoticeElapsed;
        noticeTimer.Tick += NoticeElapsed;
        noticeTimer.Start();
    }

    private void NoticeElapsed(object? sender, EventArgs e)
    {
        noticeTimer?.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (NoticeHost.Opacity <= .01) NoticeHost.Visibility = Visibility.Collapsed; };
        NoticeHost.BeginAnimation(OpacityProperty, fade);
    }
}
