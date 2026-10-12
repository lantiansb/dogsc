using System.IO;
using System.Text.Json;
using System.Windows;

namespace FrameTrail.App;

internal static class FloatingWindowPlacement
{
    private record Position(double Left, double Top);

    public static void Attach(Window window, string key)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", key + "-position.json");
        Position? position = null;
        try { if (File.Exists(file)) position = JsonSerializer.Deserialize<Position>(File.ReadAllText(file)); }
        catch (Exception error) { App.Log(error); }
        if (position is not null && double.IsFinite(position.Left) && double.IsFinite(position.Top))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = position.Left;
            window.Top = position.Top;
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Loaded += (_, _) =>
        {
            var area = SurfaceWindow.WorkArea(window);
            window.Left = Math.Clamp(window.Left, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.ActualWidth - 8));
            window.Top = Math.Clamp(window.Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.ActualHeight - 8));
        };
        void Save()
        {
            if (!double.IsFinite(window.Left) || !double.IsFinite(window.Top)) return;
            try { MemoStorage.SaveText(file, JsonSerializer.Serialize(new Position(window.Left, window.Top))); }
            catch (Exception error) { App.Log(error); }
        }
        // Mac saves the panel frame while it is dragged; save a moment after the last move so a crash keeps the place.
        // Moves during startup (initial placement / clamping) are ignored so they never become a "saved" position.
        var userMoves = false;
        var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() => userMoves = true), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.LocationChanged += (_, _) => { if (!userMoves) return; saveTimer.Stop(); saveTimer.Start(); };
        window.Closed += (_, _) => { saveTimer.Stop(); Save(); };
    }
}
