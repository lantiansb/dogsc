using System.Windows;
using FrameTrail.Media;

namespace FrameTrail.App;

// Export entry (toolbar button, Ctrl+E, export-selection shortcut): builds the panel for the current project and
// range and keeps a reference to it so closing/exiting/opening another project can see a running export.
public partial class MainWindow
{
    private Task ShowExportWindowAsync(bool audio)
    {
        if (project == null || projectFile == null) throw new InvalidOperationException("请先打开或录制一个项目。");
        if (exportWindow is { } open) { open.Activate(); return Task.CompletedTask; }
        if (operationCancellation != null) throw new InvalidOperationException("正在处理媒体，请等待完成后再导出。");
        StopPlayback();
        var full = Clone(project);
        var snapshot = ExportSnapshot(); // trimmed to the I/O range when one is set
        // Mac presentSelectedPrimaryRangeExport: the sheet becomes "导出选区" and can fall back to the whole film.
        ExportRangeInfo? range = null;
        if (TimelineView.RangeStart is { } start && TimelineView.RangeEnd is { } end && end > start)
        {
            var rangeStart = Math.Max(0, start);
            var rangeEnd = Math.Min(end, full.Timeline.Duration);
            if (rangeEnd > rangeStart) range = new ExportRangeInfo(rangeStart, rangeEnd, full);
        }
        var file = projectFile;
        var source = FrameTrailProjectStore.ResolveMediaPath(file, snapshot);
        ExportWindow? window = null;
        window = new ExportWindow(snapshot, PreviewImage.Source, ProjectTitle.Text, (path, options, progress, token) =>
        {
            var active = window!.ActiveProject;
            return Task.Run(() => new ExportEngine().ExportAsync(active, source, path, options with { ProjectFilePath = file }, progress, token));
        }, initialAudioOnly: audio, range: range) { Owner = this };
        exportWindow = window;
        try { window.ShowDialog(); }
        finally { exportWindow = null; }
        return Task.CompletedTask;
    }
}
