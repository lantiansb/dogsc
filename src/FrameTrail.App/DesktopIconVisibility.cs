using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameTrail.App;

/// <summary>
/// A hidden child owns Explorer state. Closing its stdin, including when the editor crashes, restores that state.
/// A durable journal also covers termination of the whole process tree; the next primary launch recovers it.
/// COM runs on the child's STA thread so an unresponsive Explorer cannot block the editor's dispatcher.
/// </summary>
internal sealed class DesktopIconVisibility : IAsyncDisposable
{
    internal const string WorkerArgument = "--desktop-icons-worker";
    internal static string JournalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FrameTrail", "desktop-icons", $"session-{Process.GetCurrentProcess().SessionId}.json");
    private readonly Process worker;
    private readonly Task<string> errorText;
    private int closing;
    private DesktopIconVisibility(Process worker)
    {
        this.worker = worker;
        errorText = worker.StandardError.ReadToEndAsync();
    }

    internal static async Task<DesktopIconVisibility> StartAsync(string? executable = null)
    {
        var session = Launch("hide", executable);
        try
        {
            var ready = await session.worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (ready != "READY") throw new IOException("桌面图标隐藏未能启动：" + await session.errorText.WaitAsync(TimeSpan.FromSeconds(2)));
            // Allow Explorer's repaint to reach the compositor before the capture pipeline takes its first frame.
            await Task.Delay(180);
            if (session.worker.HasExited) throw new IOException("桌面图标隐藏进程已退出。");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal static async Task RecoverAsync(string? executable = null)
    {
        if (!File.Exists(JournalPath)) return;
        await using var recovery = Launch("recover", executable);
        await recovery.worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (recovery.worker.ExitCode != 0) throw new IOException("桌面图标恢复暂未完成：" + await recovery.errorText);
    }

    private static DesktopIconVisibility Launch(string mode, string? executable)
    {
        var exe = executable ?? Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("找不到桌面图标辅助程序。", exe);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(mode);
        using var owner = Process.GetCurrentProcess();
        start.ArgumentList.Add(owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new DesktopIconVisibility(Process.Start(start) ?? throw new IOException("桌面图标辅助程序未能启动。"));
    }

    internal async Task ObserveExitAsync(Action<Exception> unexpectedExit)
    {
        try
        {
            await worker.WaitForExitAsync();
            if (Volatile.Read(ref closing) == 0)
                unexpectedExit(new IOException("桌面图标隐藏已中断。" + await errorText));
        }
        catch (ObjectDisposedException) { }
        catch (Exception error) { if (Volatile.Read(ref closing) == 0) unexpectedExit(error); }
    }

    internal void EnsureActive()
    {
        if (worker.HasExited) throw new IOException("桌面图标隐藏已中断，请重新开始录制。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        try
        {
            worker.StandardInput.Close();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (worker.ExitCode != 0) throw new IOException("桌面图标恢复暂未完成：" + await errorText);
        }
        finally { worker.Dispose(); }
    }

    // Called before creating any windows or acquiring the application's single-instance lock.
    internal static int RunWorker(string[] args)
    {
        if (args.Length != 4 || args[0] != WorkerArgument || args[1] is not ("hide" or "recover") ||
            !int.TryParse(args[2], out var ownerId) || !long.TryParse(args[3], out var ownerStart)) return 2;
        using var mutex = new Mutex(false, @"Local\FrameTrail.DesktopIconVisibility");
        var acquired = false;
        DesktopIconLease? lease = null;
        var result = 0;
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var errors = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            using var owner = Process.GetProcessById(ownerId);
            if (owner.StartTime.ToUniversalTime().Ticks != ownerStart || owner.SessionId != Process.GetCurrentProcess().SessionId)
                throw new IOException("录制进程身份已改变。");
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                if (args[1] == "recover") return 0; // An active recording owns the desktop; leave it alone.
                throw new IOException("另一段录制正在管理桌面图标。");
            }
            lease = new DesktopIconLease(new DesktopIconShell(), JournalPath);
            lease.Recover();
            if (args[1] == "hide")
            {
                var end = Task.Run(Console.In.ReadLine);
                if (end.IsCompleted || owner.HasExited) return 0;
                lease.Begin();
                output.WriteLine("READY");
                var explorer = DesktopIconShell.ExplorerProcessId;
                // No frame processing: only check the lifetime pipe and Explorer's process identity.
                while (!end.Wait(250) && !owner.HasExited)
                {
                    var current = DesktopIconShell.ExplorerProcessId;
                    if (current == 0 || current == explorer) continue;
                    try { lease.ExplorerRestarted(); explorer = current; }
                    catch (COMException) { /* Explorer may still be creating its desktop; retry next tick. */ }
                }
            }
        }
        catch (Exception error) { errors.WriteLine(error.Message); result = 1; }
        finally
        {
            if (lease is not null)
            {
                // Brief retries cover Explorer being restarted during a stop/crash.
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    try { lease.Dispose(); break; }
                    catch (Exception error)
                    {
                        if (attempt == 11) { errors.WriteLine(error.Message); result = 1; }
                        else Thread.Sleep(250);
                    }
                }
            }
            if (acquired) mutex.ReleaseMutex();
        }
        return result;
    }
}
