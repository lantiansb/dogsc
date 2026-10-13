using System.IO;
using System.Text.Json;

namespace FrameTrail.App;

/// <summary>Write-ahead recovery for the one visibility bit owned by a recording.</summary>
internal sealed class DesktopIconLease(IDesktopIconShell shell, string journalPath) : IDisposable
{
    private bool changed;
    internal bool Changed => changed;
    private sealed record Journal(int Version, bool OriginallyHidden);

    internal void Begin()
    {
        Recover();
        if (shell.Hidden) return; // Respect a desktop the user already keeps hidden.
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Journal(1, false));
        using (var stream = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        changed = true; // Restore even if Explorer applies the change then reports an error.
        shell.SetHidden(true);
    }

    internal void Recover()
    {
        if (!File.Exists(journalPath)) return;
        if (new FileInfo(journalPath).Length > 1024) throw new IOException("桌面图标恢复记录无效。");
        var entry = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
        if (entry is not { Version: 1, OriginallyHidden: false }) throw new IOException("桌面图标恢复记录无效。");
        // If the user has already made the icons visible, simply finish the recovery.
        if (shell.Hidden) shell.SetHidden(false);
        File.Delete(journalPath); // Keep the journal when Explorer or the disk is temporarily unavailable.
        changed = false;
    }

    internal void ExplorerRestarted()
    {
        if (changed && !shell.Hidden) shell.SetHidden(true);
    }

    public void Dispose() { if (changed) Recover(); }
}
