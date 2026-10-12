using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace FrameTrail.App;

internal static class FloatingWindowPlacement
{
    private record Position(double Left, double Top);

    // Windows that are currently attached, so a window opening in the same place (the recording bar
    // replacing the recorder setup bar) can start where the other one is right now.
    private static readonly Dictionary<string, WeakReference<Window>> Live = new();

    /// <summary>
    /// Remembers a floating window's position under <paramref name="key"/>.
    /// With <paramref name="fallbackKey"/>, a window that has no saved position of its own first appears
    /// where the window of that key currently is (or was last saved) - Mac RecorderPanelController keeps one
    /// panel frame for setup and recording; here the recording bar starts where the setup bar just was.
    /// The position is also saved shortly after the user moves the window, not only when it closes.
    /// </summary>
    public static void Attach(Window window, string key, string? fallbackKey = null)
    {
        var file = FileFor(key);
        var position = Read(file);
        if (IsValid(position))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = position!.Left;
            window.Top = position.Top;
        }
        else if (fallbackKey is not null && TryFallbackLocation(window, fallbackKey, out var fallback))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = fallback.X;
            window.Top = fallback.Y;
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        lock (Live) Live[key] = new WeakReference<Window>(window);
        var armed = false;
        var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(window, file); };
        window.Loaded += (_, _) =>
        {
            var area = SurfaceWindow.WorkArea(window);
            window.Left = Math.Clamp(window.Left, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.ActualWidth - 8));
            window.Top = Math.Clamp(window.Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.ActualHeight - 8));
            // Moves made while the window is being placed are not the user's choice.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => armed = true));
        };
        window.LocationChanged += (_, _) =>
        {
            if (!armed) return;
            saveTimer.Stop();
            saveTimer.Start();
        };
        window.Closed += (_, _) =>
        {
            saveTimer.Stop();
            lock (Live)
            {
                if (Live.TryGetValue(key, out var current) && (!current.TryGetTarget(out var target) || ReferenceEquals(target, window)))
                    Live.Remove(key);
            }
            Save(window, file);
        };
    }

    /// <summary>The window currently attached under <paramref name="key"/>, if it is still open.</summary>
    internal static Window? FindLive(string key)
    {
        lock (Live)
            return Live.TryGetValue(key, out var reference) && reference.TryGetTarget(out var window) && window.IsLoaded ? window : null;
    }

    private static bool TryFallbackLocation(Window window, string fallbackKey, out Point location)
    {
        location = default;
        if (FindLive(fallbackKey) is { } live && double.IsFinite(live.Left) && double.IsFinite(live.Top))
        {
            var left = live.Left;
            // Keep the bar centred under the setup bar's horizontal extent when both widths are known.
            if (double.IsFinite(window.Width) && live.ActualWidth > 0) left += (live.ActualWidth - window.Width) / 2;
            location = new Point(left, live.Top);
            return true;
        }
        var saved = Read(FileFor(fallbackKey));
        if (!IsValid(saved)) return false;
        location = new Point(saved!.Left, saved.Top);
        return true;
    }

    private static string FileFor(string key) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", key + "-position.json");

    private static Position? Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Position>(File.ReadAllText(file)) : null; }
        catch (Exception error) { App.Log(error); return null; }
    }

    private static bool IsValid(Position? position) =>
        position is not null && double.IsFinite(position.Left) && double.IsFinite(position.Top);

    private static void Save(Window window, string file)
    {
        if (!double.IsFinite(window.Left) || !double.IsFinite(window.Top)) return;
        try { MemoStorage.SaveText(file, JsonSerializer.Serialize(new Position(window.Left, window.Top))); }
        catch (Exception error) { App.Log(error); }
    }
}
