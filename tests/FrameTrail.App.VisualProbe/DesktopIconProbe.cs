using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameTrail.App;
using FrameTrail.App.VisualProbe;
using FrameTrail.Capture;

internal static class DesktopIconProbe
{
    private sealed class Shell : IDesktopIconShell
    {
        public bool Hidden { get; set; }
        public bool FailRestore;
        public bool FailAfterHide;
        public int Writes;
        public void SetHidden(bool hidden)
        {
            if (!hidden && FailRestore) throw new IOException("simulated Explorer restart");
            Hidden = hidden; Writes++;
            if (hidden && FailAfterHide) throw new IOException("simulated interrupted hide");
        }
    }

    internal static int Run(string[] args)
    {
        if (args[0] == "--desktop-icons-owner")
        {
            using var session = new AsyncLease(DesktopIconVisibility.StartAsync(args[1]).GetAwaiter().GetResult());
            Console.WriteLine("READY");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        if (args[0] == "--desktop-icons-readonly")
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Hidden = new DesktopIconShell().Hidden, Explorer = DesktopIconShell.ExplorerProcessId }));
            return 0;
        }
        if (args[0] == "--desktop-icons-native") Native(output, args[2], Check);
        else if (args[0] == "--desktop-icons-ui") UserInterface(output, Check);
        else
        {
            var journal = Path.Combine(output, "journal.json");
            var shell = new Shell();
            using (var lease = new DesktopIconLease(shell, journal))
            {
                lease.Begin();
                Check(shell.Hidden && File.Exists(journal), "visible desktop is hidden after a durable recovery record is written");
                shell.Hidden = false;
                lease.ExplorerRestarted();
                Check(shell.Hidden, "a replaced Explorer desktop is hidden again while recording");
            }
            Check(!shell.Hidden && !File.Exists(journal), "normal completion restores the original visible state");
            shell.Hidden = true; shell.Writes = 0;
            using (var lease = new DesktopIconLease(shell, journal)) lease.Begin();
            Check(shell.Hidden && shell.Writes == 0 && !File.Exists(journal), "already hidden icons remain untouched");
            shell.Hidden = false;
            var abandoned = new DesktopIconLease(shell, journal);
            abandoned.Begin();
            new DesktopIconLease(shell, journal).Recover();
            Check(!shell.Hidden && !File.Exists(journal), "next launch recovers a persisted interrupted recording");
            using (var lease = new DesktopIconLease(shell, journal))
            {
                lease.Begin(); shell.Hidden = false; var writes = shell.Writes;
                lease.Dispose();
                Check(shell.Writes == writes, "user restored visibility is respected without another Shell write");
            }
            var failed = new DesktopIconLease(shell, journal);
            failed.Begin(); shell.FailRestore = true;
            try { failed.Dispose(); throw new InvalidOperationException("restore error expected"); }
            catch (IOException) { }
            Check(File.Exists(journal) && shell.Hidden, "failed restoration retains its recovery record");
            shell.FailRestore = false; failed.Dispose();
            Check(!shell.Hidden && !File.Exists(journal), "retry completes restoration and clears the record");
            shell.FailAfterHide = true;
            using (var lease = new DesktopIconLease(shell, journal))
            {
                try { lease.Begin(); throw new InvalidOperationException("partial hide error expected"); }
                catch (IOException) { }
            }
            Check(!shell.Hidden && !File.Exists(journal), "startup failure after Shell applies hiding still restores visibility");
            File.WriteAllText(journal, "{\"Version\":99}");
            var writesBefore = shell.Writes;
            try { new DesktopIconLease(shell, journal).Recover(); throw new InvalidOperationException("invalid journal accepted"); }
            catch (IOException) { }
            Check(shell.Writes == writesBefore, "invalid recovery records never alter desktop state");
            File.Delete(journal);
            Check(!new FrameTrail.App.MainWindow.UserPreferences().HideDesktopIconsDuringRecording, "new users default to hiding disabled");
        }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Mode = args[0], Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {checks.Count} desktop icon checks; {output}");
        return 0;
    }

    private static void Native(string output, string appExe, Action<bool, string> check)
    {
        if (File.Exists(DesktopIconVisibility.JournalPath)) throw new IOException("Resolve any existing desktop icon session before the native probe.");
        var shell = new DesktopIconShell();
        var original = shell.Hidden;
        Process? owner = null;
        try
        {
            using (var session = new AsyncLease(DesktopIconVisibility.StartAsync(appExe).GetAwaiter().GetResult()))
            {
                check(shell.Hidden, "real Explorer reports icons hidden before capture");
                var capture = new FrameCaptureService();
                try
                {
                    var display = capture.EnumerateDisplaysAsync().GetAwaiter().GetResult().First();
                    var file = Path.Combine(output, "desktop-icons-hidden.mp4");
                    capture.StartAsync(new RecordingRequest(file,
                        new CaptureTarget(CaptureTargetKind.Display, display.Id, display.Bounds),
                        new VideoOptions(FrameRate: 30, Width: 1280, Height: 720), new AudioOptions())).GetAwaiter().GetResult();
                    Thread.Sleep(1600);
                    capture.PauseAsync().GetAwaiter().GetResult();
                    check(shell.Hidden, "paused native recording keeps icons hidden");
                    Thread.Sleep(300);
                    capture.ResumeAsync().GetAwaiter().GetResult();
                    Thread.Sleep(800);
                    capture.StopAsync().GetAwaiter().GetResult();
                    check(File.Exists(file) && new FileInfo(file).Length > 1000, "native recording with pause and resume produced media");
                }
                finally { capture.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            check(shell.Hidden == original && !File.Exists(DesktopIconVisibility.JournalPath), "normal stop restores the original real desktop state");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(DesktopIconProbe).Assembly.Location);
            start.ArgumentList.Add("--desktop-icons-owner"); start.ArgumentList.Add(appExe);
            owner = Process.Start(start)!;
            var ready = owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            check(ready == "READY" && shell.Hidden, "separate recording owner acquired real desktop hiding");
            owner.Kill(); owner.WaitForExit(); // The guard survives this crash and observes the closed lifetime pipe.
            var until = Stopwatch.StartNew();
            while ((shell.Hidden != original || File.Exists(DesktopIconVisibility.JournalPath)) && until.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(100);
            check(shell.Hidden == original && !File.Exists(DesktopIconVisibility.JournalPath), "owner process crash restores icons without restarting DogSC");
            owner.Dispose();
            owner = Process.Start(start)!;
            ready = owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            check(ready == "READY" && shell.Hidden, "a second owner acquired hiding before process-tree termination");
            owner.Kill(entireProcessTree: true); owner.WaitForExit();
            DesktopIconVisibility.RecoverAsync(appExe).GetAwaiter().GetResult();
            check(shell.Hidden == original && !File.Exists(DesktopIconVisibility.JournalPath), "persisted recovery survives termination of both the app and its guard");
        }
        finally
        {
            if (owner is not null) { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(); } owner.Dispose(); }
            DesktopIconVisibility.RecoverAsync(appExe).GetAwaiter().GetResult();
            if (shell.Hidden != original) shell.SetHidden(original);
        }
    }

    private sealed class AsyncLease(DesktopIconVisibility value) : IDisposable
    { public void Dispose() => value.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

    private static void UserInterface(string output, Action<bool, string> check)
    {
        var app = new App(startDesktop: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var editor = new MainWindow();
        const BindingFlags binding = BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                AppTheme.SetMode(theme);
                var rows = new StackPanel();
                foreach (var enabled in new[] { false, true })
                {
                    // Set initial state before event handlers attach: never write the user's settings.
                    typeof(MainWindow).GetField("hideDesktopIconsDuringRecording", binding)!.SetValue(editor, enabled);
                    var toggle = (CheckBox)typeof(MainWindow).GetMethod("CreateHideDesktopIconsToggle", binding)!.Invoke(editor, [new Thickness(10, 7, 10, 4)])!;
                    rows.Children.Add(toggle);
                }
                var surface = new Border { Width = 330, Padding = new Thickness(12), CornerRadius = new CornerRadius(16), Child = rows };
                surface.SetResourceReference(Border.BackgroundProperty, "Surface");
                surface.Measure(new Size(330, double.PositiveInfinity));
                surface.Arrange(new Rect(new Point(), surface.DesiredSize));
                surface.UpdateLayout();
                foreach (var toggle in rows.Children.OfType<CheckBox>())
                {
                    toggle.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var thumb = toggle.Template.FindName("SwitchThumb", toggle) as FrameworkElement;
                    var label = Descendants(toggle).OfType<ContentPresenter>().First();
                    var labelBounds = label.TransformToAncestor(surface).TransformBounds(new Rect(label.RenderSize));
                    check(thumb is not null && labelBounds.Right < 290 && labelBounds.Width > 160,
                        $"{theme}: switch and full label fit the compact recording configuration");
                    check(thumb?.RenderTransform is TranslateTransform translation && translation.X == (toggle.IsChecked == true ? 16 : 0),
                        $"{theme}: switch thumb reflects its initial saved state");
                }
                surface.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                var bitmap = new RenderTargetBitmap(330, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"desktop-icons-{theme}.png")); encoder.Save(stream);
            }
        }
        finally { MainWindowProbeLifetime.Close(editor); app.Shutdown(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
