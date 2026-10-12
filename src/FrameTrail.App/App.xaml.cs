using System.IO;
using System.Windows;

namespace FrameTrail.App;
public partial class App : Application
{
    private readonly UnhandledErrorPresenter unhandledErrors = new(Log, UnhandledErrorPresenter.Show);
    private DesktopInstance? desktopInstance;
    private readonly bool startDesktop = true;

    public App() { }
    internal App(bool startDesktop) => this.startDesktop = startDesktop;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Offscreen probes pump the dispatcher, which also drains WPF's queued
        // startup callback. They need resources without launching the desktop/IPC.
        if (!startDesktop) return;
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            unhandledErrors.Report(args.Exception);
        };
        base.OnStartup(e);
        // Mac AppPreferences: fresh installs start dark; "system" follows the OS. Apply before any window exists.
        AppTheme.InitializeFromSettings();
        if (IsDiagnosticLaunch(e.Args))
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            desktopInstance = new DesktopInstance();
            if (!desktopInstance.IsPrimary)
            {
                if (!await desktopInstance.SendToPrimaryAsync(e.Args, CancellationToken.None))
                {
                    var message = SurfaceDialog.Create(null, "DogSC 已在运行，但暂时没有响应。请从任务栏通知区域打开 DogSC，稍后再试。", "DogSC 正在运行", MessageBoxButton.OK, _ => { });
                    message.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    message.ShowInTaskbar = true;
                    message.ShowDialog();
                }
                Shutdown();
                return;
            }
            MainWindowPlacement.PersistenceEnabled = true; // remember/restore the editor window geometry
            var editor = new MainWindow();
            MainWindow = editor;
            var initialization = editor.StartDesktopAsync();
            editor.HandleDesktopLaunch(e.Args);
            desktopInstance.StartListening(editor.HandleDesktopLaunch);
            _ = ObserveDesktopListenerAsync(desktopInstance.ListeningTask, editor);
            await initialization;
        }
        catch (Exception error)
        {
            Log(error);
            if (desktopInstance is { IsPrimary: false })
            {
                var message = SurfaceDialog.Create(null, "未能唤回正在运行的 DogSC：" + error.Message, "DogSC 正在运行", MessageBoxButton.OK, _ => { });
                message.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                message.ShowInTaskbar = true;
                message.ShowDialog();
                Shutdown();
                return;
            }
            if (MainWindow is MainWindow editor) editor.ShowEditorForDesktop();
            else { MainWindow = new MainWindow(); MainWindow.Show(); }
            unhandledErrors.Report(error);
        }
    }

    internal static bool IsDiagnosticLaunch(IEnumerable<string> arguments) => arguments.Any(argument =>
        argument == "--visual-states" || argument == "--smoke" || argument.EndsWith("-smoke", StringComparison.Ordinal));

    private static async Task ObserveDesktopListenerAsync(Task listener, MainWindow editor)
    {
        try { await listener; }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log(error); editor.ReportDesktopFailure(error); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow editor) editor.DisposeDesktop();
        desktopInstance?.Dispose();
        desktopInstance = null;
        base.OnExit(e);
    }
    public static void Log(Exception ex)
    {
        try { var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "errors.log"), DateTimeOffset.Now.ToString("O") + " " + ex + Environment.NewLine); } catch { }
    }
}
