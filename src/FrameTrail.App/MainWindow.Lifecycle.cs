using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using FrameTrail.Capture;

namespace FrameTrail.App;

// Project lifecycle and window shell behaviour (Mac AppModel+ProjectCommands / EditorWindowController):
// inline title rename, save-state dot, close/delete/open guards, theme and focus-loss hooks.
public partial class MainWindow
{
    private ExportWindow? exportWindow;
    private string? freshRecordingProject;
    private readonly HashSet<string> openedRecordingProjects = new(StringComparer.OrdinalIgnoreCase);
    private bool titleEditing;

    /// <summary>Called once from the constructor after InitializeComponent.</summary>
    private void InitializeLifecycle()
    {
        // Mac EditorWindowLifecycleBridge: losing key status ends in-flight drags. The controls own their
        // gesture cancellation (LostMouseCapture); this only makes sure the capture is let go.
        Deactivated += (_, _) => ReleaseGestureCaptures();
        Action followSystem = () => { RefreshThemeDependents(); };
        AppTheme.SystemChanged += followSystem;
        Closed += (_, _) => AppTheme.SystemChanged -= followSystem;
        RefreshThemeDependents(rebuildInspector: false);
        SetSaveState(true);
    }

    // ---- E24: focus loss ----------------------------------------------------------------------------------

    private void ReleaseGestureCaptures()
    {
        try
        {
            if (Mouse.Captured is DependencyObject captured && ReferenceEquals(GetWindow(captured), this))
                Mouse.Capture(null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { App.Log(ex); }
    }

    // ---- E19: save state dot ------------------------------------------------------------------------------

    /// <summary>Dot next to the project title: green = saved, red = the last save failed.</summary>
    private void SetSaveState(bool saved, string? error = null)
    {
        if (SaveDot is null) return;
        if (saved) SaveDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        else SaveDot.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
        var text = saved ? "已保存" : "保存失败" + (string.IsNullOrWhiteSpace(error) ? "" : "：" + error);
        SaveDot.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(SaveDot, "保存状态：" + text);
    }

    // ---- E10 / E19: inline project title ------------------------------------------------------------------

    private void ProjectTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;
        _ = Guard(() => { BeginProjectTitleEdit(); return Task.CompletedTask; });
    }

    private void RenameProjectClick(object sender, RoutedEventArgs e) => _ = Guard(() => { BeginProjectTitleEdit(); return Task.CompletedTask; });

    private void BeginProjectTitleEdit()
    {
        if (project is null || projectFile is null) throw new InvalidOperationException("请先打开或录制一个项目。");
        if (titleEditing) return;
        titleEditing = true;
        ProjectTitleEditor.Text = project.DisplayName ?? ProjectTitle.Text;
        ProjectTitle.Visibility = Visibility.Collapsed;
        ProjectTitleEditor.Visibility = Visibility.Visible;
        ProjectTitleEditor.Focus();
        ProjectTitleEditor.SelectAll();
    }

    private void ProjectTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndProjectTitleEdit(commit: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndProjectTitleEdit(commit: false); e.Handled = true; }
    }

    private void ProjectTitleEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndProjectTitleEdit(commit: true);

    /// <summary>Mac renames through the undo stack ("重命名项目"), so undo restores the old title as expected.</summary>
    private void EndProjectTitleEdit(bool commit)
    {
        if (!titleEditing) return;
        titleEditing = false;
        var name = ProjectTitleEditor.Text.Trim();
        // Hand keyboard focus back to the window so editor shortcuts (Space, Ctrl+Z…) work again.
        if (ProjectTitleEditor.IsKeyboardFocusWithin) { Keyboard.ClearFocus(); Focus(); }
        ProjectTitleEditor.Visibility = Visibility.Collapsed;
        ProjectTitle.Visibility = Visibility.Visible;
        if (!commit || project is null) return;
        if (name.Length is 0 or > 240) { ShowNotice("项目名称长度应为 1–240 个字符。", warning: true); return; }
        _ = Guard(() => { Edit(p => p.DisplayName = name, "重命名项目"); return Task.CompletedTask; });
    }

    // ---- E14: opening other projects ----------------------------------------------------------------------

    /// <summary>Mac requestOpenProject refuses while exporting, swapping media or recording.</summary>
    private void EnsureCanSwitchProject()
    {
        if (exportWindow is { IsExporting: true } || operationCancellation != null)
            throw new InvalidOperationException("正在导出或处理媒体，请等待完成或先取消后再打开其他项目。");
        if (capture.State is CaptureState.Recording or CaptureState.Starting or CaptureState.Paused && !recordingFinalizing)
            throw new InvalidOperationException("正在录制，请先结束录制再打开其他项目。");
    }

    /// <summary>The current project is written to disk before another one replaces it.</summary>
    private void SaveBeforeSwitchingProject()
    {
        if (project is null || projectFile is null) return;
        try { FrameTrailProjectStore.Save(projectFile, project); }
        catch (Exception ex) { App.Log(ex); SetSaveState(false, ex.Message); }
    }

    /// <summary>True only the first time a project from this session's recording is opened (it has never been edited).</summary>
    private bool IsFirstOpenOfRecordedProject(string path)
    {
        if (recordingFolder is null) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(recordingFolder, "project.frametrail"));
            return string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase) && openedRecordingProjects.Add(expected);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    // ---- E11 / E13: closing and deleting -------------------------------------------------------------------

    private bool IsFreshUneditedRecording() =>
        project is not null && projectFile is not null && freshRecordingProject is not null
        && string.Equals(freshRecordingProject, Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase)
        && history is { CanUndo: false, CanRedo: false };

    /// <summary>
    /// Mac requestCloseProject: a running export is cancelled first (after asking); a brand-new recording that was never
    /// edited asks "保留 / 移到回收站 / 取消". Returns false when the user cancels the close.
    /// </summary>
    private async Task<bool> ConfirmCloseEditorAsync()
    {
        if (exportWindow is { IsExporting: true } exporting)
        {
            var answer = SurfaceDialog.Show(exporting, "正在导出。关闭编辑器会取消导出并清理未完成的文件，是否继续？", "取消导出", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;
            await exporting.CancelAndCloseAsync();
        }
        if (!IsFreshUneditedRecording()) return true;
        var choice = SurfaceDialog.Show(DesktopDialogOwner(), "这是刚录制、还没有编辑过的项目。关闭编辑器前，要保留这次录制吗？\n选择“移到回收站”会把整个录制文件夹（含原始素材）移入回收站。",
            "保留这次录制？", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, acceptLabel: "移到回收站", noLabel: "保留", cancelLabel: "取消");
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes) await RemoveCurrentProjectAsync(includeMedia: true);
        else freshRecordingProject = null; // kept on purpose: do not ask again for this project
        return true;
    }

    private async Task WaitForOperationEndAsync()
    {
        var waited = Stopwatch.StartNew();
        while (operationCancellation != null && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
    }

    /// <summary>Hides the resident editor: saves, then lets go of playback resources (Mac closeProject releases the session).</summary>
    private async Task HideEditorForDesktopAsync()
    {
        SaveBeforeSwitchingProject();
        StopPlayback();
        seekCancellation?.Cancel();
        ClosePlaybackAudio();
        await DisposePlaybackResourcesAsync();
        // E11 (Mac closeProject): closing the editor unloads the project (saved above) and returns to the empty state, so the next
        // "open editor" shows recent projects instead of a stale project. A running media task keeps its project until it ends.
        // The recording result card opens its project afresh (OpenProject), so "进入编辑" does not depend on a loaded project.
        if (project is not null && operationCancellation is null)
        {
            await ReleaseProjectMediaAsync();
            ClearProjectState();
        }
        Hide();
    }

    private async void DeleteProjectClick(object sender, RoutedEventArgs e) => await Guard(DeleteCurrentProjectAsync);

    private async Task DeleteCurrentProjectAsync()
    {
        if (project is null || projectFile is null) throw new InvalidOperationException("当前没有打开的项目。");
        if (exportWindow is { IsExporting: true } || operationCancellation != null) throw new InvalidOperationException("正在导出或处理媒体，请等待完成后再删除项目。");
        if (capture.State is CaptureState.Recording or CaptureState.Starting or CaptureState.Paused) throw new InvalidOperationException("正在录制，请先结束录制再删除项目。");
        var folder = Path.GetDirectoryName(projectFile)!;
        bool includeMedia;
        if (IsOwnedProjectFolder(folder))
        {
            var answer = SurfaceDialog.Show(this,
                "要怎样删除当前项目？\n\n• 仅移除工程文件：只把 .frametrail 项目文件移入回收站，原始视频、音频等素材保留在磁盘。\n• 连素材移到回收站：把整个项目文件夹（含录制素材）移入回收站，可从回收站还原。",
                "删除项目", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, acceptLabel: "连素材移到回收站", noLabel: "仅移除工程文件", cancelLabel: "取消");
            if (answer == MessageBoxResult.Cancel) return;
            includeMedia = answer == MessageBoxResult.Yes;
        }
        else
        {
            // Projects opened from arbitrary folders may share the folder with unrelated files: never trash the folder.
            var answer = SurfaceDialog.Show(this, "只会把当前 .frametrail 项目文件移入回收站。项目文件夹中的原始视频、音频和其他素材都会保留。是否继续？", "移除项目文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            includeMedia = false;
        }
        await RemoveCurrentProjectAsync(includeMedia);
        ShowNotice(includeMedia ? "项目和录制素材已移入回收站。" : "项目文件已移入回收站，原始素材仍保留在磁盘。");
    }

    /// <summary>Files DogSC itself writes into every folder it creates (recording journal / controls / result, DogSC import report).</summary>
    private static readonly string[] OwnedFolderMarkers =
        ["recording-recovery.json", "recording-recovery.json.recovered", "recording-controls.json", "capture-result.json", "dogsc-import-report.json"];

    /// <summary>
    /// Only a folder DogSC created itself may be moved to the recycle bin as a whole: a direct child of one of its recording / import roots
    /// that carries a file DogSC writes (<see cref="OwnedFolderMarkers"/>), or an "Import-*" video-import folder with its media/ subfolder.
    /// A user's own folder nested deeper under those roots is never treated as owned; then only the .frametrail file is recycled.
    /// </summary>
    private bool IsOwnedProjectFolder(string folder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (recordingFolder is not null && string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingFolder)), StringComparison.OrdinalIgnoreCase)) return true;
            var parent = Path.GetDirectoryName(full);
            if (parent is null) return false;
            parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            foreach (var root in new[] { ProjectsRoot, Path.Combine(videos, "FrameTrail"), Path.Combine(videos, "DogSC"), Path.Combine(videos, "FrameTrail", "Imported") })
            {
                if (!string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)) continue;
                if (OwnedFolderMarkers.Any(marker => File.Exists(Path.Combine(full, marker)))) return true;
                if (Path.GetFileName(full).StartsWith("Import-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(full, "media"))
                    && File.Exists(Path.Combine(full, "project.frametrail"))) return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return false;
    }

    /// <summary>Stops everything using the project's files, recycles the project (file or whole folder) and empties the editor.</summary>
    private async Task RemoveCurrentProjectAsync(bool includeMedia)
    {
        if (projectFile is null) return;
        var file = projectFile;
        var folder = Path.GetDirectoryName(file)!;
        await ReleaseProjectMediaAsync();
        try
        {
            if (includeMedia) FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Nothing was removed: reopen the preview resources lazily and keep the project loaded.
            _ = Guard(() => RenderPreview(Playhead.Value));
            throw new IOException("无法把项目移入回收站：" + ex.Message, ex);
        }
        RecentProjects.Remove(file);
        ClearProjectState();
    }

    /// <summary>Releases playback, scrubbing and preview handles on the media files (needed before recycling them).</summary>
    private async Task ReleaseProjectMediaAsync()
    {
        StopPlayback();
        seekCancellation?.Cancel();
        assetsCancellation?.Cancel();
        ClearLiveEdit();
        ClosePlaybackAudio();
        Player.Source = null;
        await DisposePlaybackResourcesAsync();
        await previewGate.WaitAsync();
        try
        {
            if (scrubDecoder is not null) await scrubDecoder.DisposeAsync();
            scrubDecoder = null; scrubDecoderKey = null; completedPreview = null;
            previewAssets?.Dispose(); previewAssets = null; previewAssetsProject = null;
        }
        finally { previewGate.Release(); }
    }

    /// <summary>Back to the empty editor: no preview, player, timeline, range or selection of the removed project.</summary>
    private void ClearProjectState()
    {
        EndProjectTitleEdit(commit: false);
        StopPlayback();
        project = null; projectFile = null; history = null; timelineAssets = null; selectedEffect = null; freshRecordingProject = null;
        displayedPreviewScene = null; displayedPreviewProject = null; evaluatedProject = null;
        TimelineView.MultiSelection.Clear(); TimelineView.RangeStart = null; TimelineView.RangeEnd = null;
        Clips.ItemsSource = null; Markers.ItemsSource = null;
        TimelineView.Project = null; TimelineView.Assets = null; TimelineView.Refresh();
        PreviewImage.Source = null; Player.Source = null;
        refreshing = true; Playhead.Maximum = 1; Playhead.Value = 0; refreshing = false;
        InvalidateProxy();
        Waveform.Children.Clear();
        EmptyState.Visibility = Visibility.Visible; ProjectTitle.Text = "未命名项目"; TimelineInfo.Text = "";
        UpdateTime(); SetSaveState(true); BuildInspector();
        RefreshEmptyRecents();
    }
}
