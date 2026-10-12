using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using FrameTrail.Capture;
using FrameTrail.Media;

namespace FrameTrail.App;

public partial class MainWindow
{
    private static readonly JsonSerializerOptions MenuJson = new() { WriteIndented = true };
    private static string PresetRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "presets");

    private void ProjectMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        AddMenu(menu, "打开 DogSC 项目…", async () => await PickProject());
        AddMenu(menu, "导入视频…", async () => await ImportVideoFromMenu());
        AddMenu(menu, "导入 macOS DogSC 项目…", async () => await ImportDogScClick());
        AddMenu(menu, "保存项目", () => { _ = Guard(() => { SaveProject(); return Task.CompletedTask; }); });
        // Deferred until the menu has closed, otherwise its focus restore would end the inline edit at once.
        AddMenu(menu, "重命名项目…", () => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { _ = Guard(() => { BeginProjectTitleEdit(); return Task.CompletedTask; }); })));
        AddMenu(menu, "删除项目…", () => DeleteProjectClick(this, new RoutedEventArgs()));
        AddUpgradeLayoutModelMenu(menu);
        var recent = new MenuItem { Header = "最近项目" };
        foreach (var path in RecentProjects.Read())
        {
            // Mac recentProjectSummaries: the project title and date, not the timestamp folder name.
            var summary = RecentProjects.Describe(path);
            var heading = new StackPanel { Width = 340, Margin = new Thickness(0, 2, 0, 2) };
            heading.Children.Add(new TextBlock { Text = summary.Title + "  ·  " + summary.DateText, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            heading.Children.Add(new TextBlock
            {
                Text = Path.GetDirectoryName(path), FontSize = 11, Opacity = .72,
                Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var item = new MenuItem { Header = heading, ToolTip = path };
            System.Windows.Automation.AutomationProperties.SetName(item, summary.Title + "  ·  " + summary.DateText + "  ·  " + path);
            item.Click += async (_, _) => await Guard(() => OpenProject(path));
            recent.Items.Add(item);
        }
        if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "暂无最近项目", IsEnabled = false });
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());
        AddMenu(menu, "在资源管理器中查看项目…", () => RevealProjectFolder());
        AddMenu(menu, "显示录制工具", () => ShowRecorderClick(this, new RoutedEventArgs()));
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private static ContextMenu NewMenu() => new() { StaysOpen = false, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
    private static void AddMenu(ItemsControl menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    private static void AddMenu(ItemsControl menu, string title, Func<Task> action)
    {
        var item = new MenuItem { Header = title };
        item.Click += async (_, _) => await action(); menu.Items.Add(item);
    }

    /// <summary>Default folder for project pickers and imports: the recording root.</summary>
    private string ProjectsRoot => preferredRecordingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail");

    /// <summary>Mac openProjectPicker starts in the projects folder: here the newest recent project's folder, else the recording root.</summary>
    private string ProjectPickerDirectory()
    {
        var recent = RecentProjects.Read().FirstOrDefault();
        var recentFolder = recent is null ? null : Path.GetDirectoryName(recent);
        if (recentFolder is not null && Directory.Exists(recentFolder)) return recentFolder;
        return Directory.Exists(ProjectsRoot) ? ProjectsRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    // The single "open project" entry (toolbar menu, shortcuts): OpenProject itself records the recent entry.
    private async Task PickProject()
    {
        var dialog = new OpenFileDialog { Title = "打开 DogSC 项目", Filter = "DogSC 项目|*.frametrail|项目备份|*.frametrail.bak|所有文件|*.*", CheckFileExists = true, InitialDirectory = ProjectPickerDirectory() };
        if (dialog.ShowDialog(this) != true) return;
        await Guard(() => OpenProject(dialog.FileName));
    }

    // The single "import video" implementation (menu and the empty-state button).
    private async Task ImportVideoFromMenu()
    {
        var pick = new OpenFileDialog { Title = "导入视频并创建项目", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm", CheckFileExists = true };
        if (pick.ShowDialog(this) != true) return;
        await Guard(async () =>
        {
            var info = await MediaTools.ProbeAsync(pick.FileName);
            if (info.Width <= 0 || info.Duration <= 0) throw new InvalidDataException("文件没有可用视频。");
            var dir = Path.Combine(ProjectsRoot, "Import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(dir, "media"));
            var relative = "media/source" + Path.GetExtension(pick.FileName).ToLowerInvariant();
            File.Copy(pick.FileName, Path.Combine(dir, relative));
            var imported = MakeProject(relative, info); imported.DisplayName = Path.GetFileNameWithoutExtension(pick.FileName);
            var file = Path.Combine(dir, "project.frametrail"); FrameTrailProjectStore.Save(file, imported);
            await OpenProject(file);
            Status.Text = "已复制素材并建立项目：" + dir;
        });
    }

    private async Task ImportDogScClick()
    {
        var dialog = new OpenFolderDialog { Title = "选择 .dogscproject 项目文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        await Guard(async () =>
        {
            var source = Path.GetFullPath(dialog.FolderName);
            var manifest = Path.Combine(source, "project.json");
            if (!Directory.Exists(source) || !Path.GetFileName(source).EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择以 .dogscproject 结尾的 DogSC 项目文件夹。");
            if (!File.Exists(manifest)) throw new FileNotFoundException("项目文件夹缺少 project.json。", manifest);
            using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest));
            var screenPath = doc.RootElement.GetProperty("media").GetProperty("screen").GetProperty("relativePath").GetString();
            if (string.IsNullOrWhiteSpace(screenPath) || Path.IsPathRooted(screenPath) || screenPath.Contains(':'))
                throw new InvalidDataException("DogSC 项目没有有效的相对屏幕素材路径。");
            var sourceMedia = Path.GetFullPath(Path.Combine(source, screenPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            var prefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
            if (!sourceMedia.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourceMedia))
                throw new InvalidDataException("DogSC 主素材路径越出项目目录或文件不存在。");
            for (var walk = sourceMedia; walk != source && walk.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); walk = Path.GetDirectoryName(walk)!)
                if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("DogSC 主素材路径经过链接或重解析点，已停止导入。");
            var info = await MediaTools.ProbeAsync(sourceMedia);
            if (info.Width <= 0 || info.Duration <= 0) throw new InvalidDataException("DogSC 主素材没有可读取的视频流。");
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail", "Imported");
            Directory.CreateDirectory(root);
            var stem = SafeProjectStem(Path.GetFileNameWithoutExtension(source));
            var destination = Path.Combine(root, stem + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".frametrail");
            var imported = DogScProjectImporter.Import(source, destination, new MediaAsset { Width = info.Width, Height = info.Height, Duration = info.Duration });
            imported = await DogScImportMetadata.ApplyCameraAspectRatioAsync(imported);
            await OpenProject(imported.ProjectFilePath);
            Status.Text = "已导入 DogSC 项目副本；原项目未修改。未映射字段请查看 dogsc-import-report.json。";
            if (imported.Report.Warnings.Count > 0)
                SurfaceDialog.Show(this, "项目已导入，部分设置未映射：\n\n" + string.Join("\n", imported.Report.Warnings.Take(12)) + (imported.Report.Warnings.Count > 12 ? "\n…详见导入报告。" : ""), "DogSC 导入提示", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private static string SafeProjectStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var name = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "Imported" : name;
    }

    private void RevealProjectFolder()
    {
        var folder = projectFile is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail") : Path.GetDirectoryName(projectFile)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static string? PromptText(Window owner, string title, string label, string initialValue)
    {
        // Keep enough client height for the label, input and action row at 100–150% DPI.
        // WPF measures these dimensions in DIPs, so the TextBox minimum remains readable
        // when Windows scales the dialog.
        var dialog = new Window { Title = title, Width = 420, Height = 236, MinHeight = 236, ShowInTaskbar = false };
        SurfaceWindow.Configure(dialog, owner);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 78, Margin = new Thickness(5, 10, 0, 0), Style = owner.TryFindResource("PrimaryButton") as Style };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 78, Margin = new Thickness(5, 10, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var stack = new StackPanel(); stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 2, 0, 4) });
        var input = new TextBox { Text = initialValue, MinHeight = 38, Padding = new Thickness(9, 7, 9, 7), VerticalContentAlignment = VerticalAlignment.Center }; stack.Children.Add(input); root.Children.Add(stack); dialog.Content = SurfaceWindow.WrapContent(dialog, root, title);
        ok.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); }; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void MarkerMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        AddMenu(menu, "在播放头添加标记…", () => AddMarkerAtPlayhead());
        if (project is { Markers.Count: > 0 })
        {
            menu.Items.Add(new Separator());
            foreach (var marker in project.Markers.OrderBy(m => m.SourceTime))
            {
                var parent = new MenuItem { Header = $"跳转到 {marker.SourceTime:0.00}s · {marker.Label}" };
                parent.Click += (_, _) => JumpToMarker(marker.Id);
                var delete = new MenuItem { Header = "删除此标记" }; delete.Click += (_, _) => ApplyEdit(p => p.Markers.RemoveAll(x => x.Id == marker.Id));
                parent.Items.Add(delete); menu.Items.Add(parent);
            }
        }
        if (project is null) menu.Items.Insert(0, new MenuItem { Header = "请先打开项目", IsEnabled = false });
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true;
    }

    private void AddMarkerAtPlayhead()
    {
        if (project is null) { ShowNotice("请先打开项目。", warning: true); return; }
        var label = PromptText(this, "添加标记", "标记名称", $"标记 {project.Markers.Count + 1}");
        if (label is null) return;
        ApplyEdit(p => p.Markers.Add(new RecordingMarker(Guid.NewGuid(), SourceNow, label.Trim().Length == 0 ? "标记" : label.Trim())));
    }

    private void AddElementClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        AddMenu(menu, "添加运镜片段", () => AddMotionClip());
        AddMenu(menu, "添加贴图…", () => AddOverlayClick(this, new RoutedEventArgs()));
        AddMenu(menu, "添加模糊遮罩", () => AddBlurClip());
        var paste = new MenuItem { Header = "粘贴图片", IsEnabled = CanPasteOverlayImage() };
        paste.Click += (_, _) => _ = Guard(() => { PasteImageAsSticker(); return Task.CompletedTask; });
        menu.Items.Add(paste);
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void PresetClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        AddMenu(menu, "简洁浅色", () => ApplyBuiltinPreset("简洁浅色", "#F4F6F6", ScreenFrameKind.None, .08, .02));
        AddMenu(menu, "深色窗口", () => ApplyBuiltinPreset("深色窗口", "#202124", ScreenFrameKind.WindowDark, .08, .025));
        AddMenu(menu, "清爽浏览器", () => ApplyBuiltinPreset("清爽浏览器", "#EAF0F2", ScreenFrameKind.BrowserLight, .07, .025));
        menu.Items.Add(new Separator());
        var custom = LoadPresetFiles();
        foreach (var entry in custom)
        {
            var item = new MenuItem { Header = entry.Name + PresetModifiedSuffix(entry.Path) };
            item.Click += (_, _) => ApplyCustomPreset(entry.Path); menu.Items.Add(item);
            var apply = new MenuItem { Header = "应用此预设" }; apply.Click += (_, _) => ApplyCustomPreset(entry.Path); item.Items.Add(apply);
            var rename = new MenuItem { Header = "重命名本机预设…" }; rename.Click += (_, _) => RenameCustomPreset(entry.Path); item.Items.Add(rename);
            var overwrite = new MenuItem { Header = "用当前样式覆盖…" }; overwrite.Click += (_, _) => OverwriteCustomPreset(entry.Path); item.Items.Add(overwrite);
            var remove = new MenuItem { Header = "删除本机预设" }; remove.Click += (_, _) => DeleteCustomPreset(entry.Path); item.Items.Add(remove);
        }
        AddMenu(menu, "将当前样式保存为本机预设…", () => SaveCustomPreset());
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void ApplyBuiltinPreset(string name, string color, ScreenFrameKind frame, double padding, double radius)
    {
        if (project is null) { ShowNotice("请先打开项目。", warning: true); return; }
        ApplyEdit(p => { p.Settings.Background = color; p.Settings.Padding = padding; p.Settings.CornerRadius = radius; p.Settings.Shadow = .28; p.Settings.BackgroundStyle = new BackgroundSettings { Color = color }; p.Settings.Canvas ??= new CanvasSettings(); p.Settings.Canvas.Frame = frame; });
        Status.Text = "已应用场景预设：" + name;
    }

    private sealed record PresetEntry(string Name, string Path);
    private sealed class SavedScenePreset { public string Name { get; set; } = ""; public FrameTrailSettings Settings { get; set; } = new(); }
    private static IReadOnlyList<PresetEntry> LoadPresetFiles()
    {
        if (!Directory.Exists(PresetRoot)) return [];
        var list = new List<PresetEntry>();
        foreach (var path in Directory.EnumerateFiles(PresetRoot, "preset.json", System.IO.SearchOption.AllDirectories))
            try { var data = JsonSerializer.Deserialize<SavedScenePreset>(File.ReadAllText(path)); if (data is not null && !string.IsNullOrWhiteSpace(data.Name)) list.Add(new(data.Name, path)); } catch { }
        return list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void SaveCustomPreset()
    {
        if (project is null || projectFile is null) { ShowNotice("请先打开项目。", warning: true); return; }
        var name = PromptText(this, "保存场景预设", "预设名称", project.DisplayName ?? "我的样式");
        if (name is null) return; name = name.Trim();
        if (name.Length is 0 or > 100) { ShowNotice("预设名称长度应为 1–100 个字符。", warning: true); return; }
        string? temporaryDirectory = null;
        try
        {
            Directory.CreateDirectory(PresetRoot);
            var id = Guid.NewGuid().ToString("N");
            var presetDirectory = Path.Combine(PresetRoot, id);
            temporaryDirectory = Path.Combine(PresetRoot, ".tmp-" + id);
            Directory.CreateDirectory(temporaryDirectory);
            var settings = CapturePresetSettings(temporaryDirectory);
            File.WriteAllText(Path.Combine(temporaryDirectory, "preset.json"), JsonSerializer.Serialize(new SavedScenePreset { Name = name, Settings = settings }, MenuJson));
            Directory.Move(temporaryDirectory, presetDirectory); temporaryDirectory = null;
            Status.Text = "已保存本机预设：" + name;
        }
        catch (Exception ex) { SurfaceDialog.Show(this, "保存预设失败：" + ex.Message, "场景预设", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { if (temporaryDirectory is not null) TryDeleteOwnedDirectory(temporaryDirectory); }
    }

    private FrameTrailSettings CapturePresetSettings(string destinationDirectory)
    {
        if (project is null || projectFile is null) throw new InvalidOperationException("请先打开项目。");
        var settings = JsonSerializer.Deserialize<FrameTrailSettings>(JsonSerializer.Serialize(project.Settings))!;
        if (settings.BackgroundStyle is { } background)
        {
            string? imagePath = background.ImagePath;
            string? videoPath = background.VideoPath;
            if (background.Kind == BackgroundKind.Image && !string.IsNullOrWhiteSpace(imagePath))
                imagePath = CopyProjectAssetToPreset(imagePath, destinationDirectory, "background-image");
            if (background.Kind == BackgroundKind.Video && !string.IsNullOrWhiteSpace(videoPath))
                videoPath = CopyProjectAssetToPreset(videoPath, destinationDirectory, "background-video");
            settings.BackgroundStyle = background with { ImagePath = imagePath, VideoPath = videoPath };
        }
        return settings;
    }

    private string CopyProjectAssetToPreset(string relativePath, string destinationDirectory, string prefix)
    {
        if (projectFile is null) throw new InvalidOperationException("请先打开项目。");
        var source = FrameTrailProjectStore.ResolveAssetPath(projectFile, relativePath);
        if (!File.Exists(source)) throw new FileNotFoundException("当前背景素材不存在。", source);
        RejectReparsePath(Path.GetDirectoryName(projectFile)!, source);
        var assetName = prefix + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source).ToLowerInvariant();
        File.Copy(source, Path.Combine(destinationDirectory, assetName));
        return assetName;
    }

    private void RenameCustomPreset(string presetFile)
    {
        try
        {
            var safeFile = ValidatePresetFile(presetFile);
            var preset = JsonSerializer.Deserialize<SavedScenePreset>(File.ReadAllText(safeFile)) ?? throw new InvalidDataException("预设文件为空。");
            var name = PromptText(this, "重命名本机预设", "预设名称", preset.Name)?.Trim();
            if (name is null) return;
            if (name.Length is 0 or > 100) throw new InvalidDataException("预设名称长度应为 1–100 个字符。");
            preset.Name = name;
            var temporaryFile = safeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllText(temporaryFile, JsonSerializer.Serialize(preset, MenuJson)); File.Move(temporaryFile, safeFile, true); }
            finally { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
            Status.Text = "已重命名本机预设：" + name;
        }
        catch (Exception ex) { SurfaceDialog.Show(this, "重命名预设失败：" + ex.Message, "场景预设", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OverwriteCustomPreset(string presetFile)
    {
        if (project is null || projectFile is null) { ShowNotice("请先打开项目。", warning: true); return; }
        if (SurfaceDialog.Show(this, "将用当前项目的样式替换此预设。已有预设图片会在新版本安全写入后再清理；如果写入失败，会恢复原预设。是否继续？", "覆盖本机预设", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        string? temporaryDirectory = null;
        string? backupDirectory = null;
        var swapped = false;
        try
        {
            var safeFile = ValidatePresetFile(presetFile);
            var directory = Path.GetDirectoryName(safeFile)!;
            var existing = JsonSerializer.Deserialize<SavedScenePreset>(File.ReadAllText(safeFile)) ?? throw new InvalidDataException("预设文件为空。");
            var id = Guid.NewGuid().ToString("N");
            temporaryDirectory = Path.Combine(PresetRoot, ".tmp-" + id);
            backupDirectory = Path.Combine(PresetRoot, ".backup-" + id);
            Directory.CreateDirectory(temporaryDirectory);
            CopyPresetAssetsSafely(directory, temporaryDirectory);
            var settings = CapturePresetSettings(temporaryDirectory);
            File.WriteAllText(Path.Combine(temporaryDirectory, "preset.json"), JsonSerializer.Serialize(new SavedScenePreset { Name = existing.Name, Settings = settings }, MenuJson));
            Directory.Move(directory, backupDirectory);
            try { Directory.Move(temporaryDirectory, directory); temporaryDirectory = null; swapped = true; }
            catch { Directory.Move(backupDirectory, directory); backupDirectory = null; throw; }
            Status.Text = "已用当前样式覆盖本机预设：" + existing.Name;
        }
        catch (Exception ex) { SurfaceDialog.Show(this, "覆盖预设失败：" + ex.Message, "场景预设", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            if (temporaryDirectory is not null) TryDeleteOwnedDirectory(temporaryDirectory);
            if (swapped && backupDirectory is not null) TryDeleteOwnedDirectory(backupDirectory);
        }
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static string ValidatePresetFile(string presetFile)
    {
        var root = Path.GetFullPath(PresetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(presetFile);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(full), "preset.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("预设文件不在 DogSC 本机预设目录中。");
        RejectReparsePath(PresetRoot, full);
        return full;
    }

    private static void CopyPresetAssetsSafely(string sourceDirectory, string destinationDirectory)
    {
        RejectReparsePath(sourceDirectory, sourceDirectory);
        var pending = new Stack<string>();
        pending.Push(sourceDirectory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                RejectReparsePath(sourceDirectory, directory);
                var relative = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
                pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                RejectReparsePath(sourceDirectory, file);
                if (string.Equals(Path.GetFileName(file), "preset.json", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(sourceDirectory, file);
                var target = Path.Combine(destinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
    }

    private void ApplyCustomPreset(string presetFile)
    {
        if (project is null || projectFile is null) { ShowNotice("请先打开项目。", warning: true); return; }
        try
        {
            var preset = JsonSerializer.Deserialize<SavedScenePreset>(File.ReadAllText(presetFile)) ?? throw new InvalidDataException("预设文件为空。");
            var settings = JsonSerializer.Deserialize<FrameTrailSettings>(JsonSerializer.Serialize(preset.Settings))!;
            if (settings.BackgroundStyle is { } background)
            {
                string? imagePath = background.ImagePath;
                string? videoPath = background.VideoPath;
                var presetDirectory = Path.GetFullPath(Path.GetDirectoryName(presetFile)!);
                if (background.Kind == BackgroundKind.Image && !string.IsNullOrWhiteSpace(imagePath))
                    imagePath = CopyPresetAssetToProject(presetDirectory, imagePath, "preset-image");
                if (background.Kind == BackgroundKind.Video && !string.IsNullOrWhiteSpace(videoPath))
                    videoPath = CopyPresetAssetToProject(presetDirectory, videoPath, "preset-video");
                settings.BackgroundStyle = background with { ImagePath = imagePath, VideoPath = videoPath };
            }
            ApplyEdit(p => { p.Settings = MergePresetStyle(p.Settings, settings); appliedPreset = (projectFile, presetFile, PresetStyleJson(p.Settings)); }); Status.Text = "已应用本机预设：" + preset.Name;
        }
        catch (Exception ex) { SurfaceDialog.Show(this, "应用预设失败：" + ex.Message, "场景预设", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private string CopyPresetAssetToProject(string presetDirectory, string assetPath, string prefix)
    {
        if (projectFile is null) throw new InvalidOperationException("请先打开项目。");
        var source = Path.GetFullPath(Path.Combine(presetDirectory, assetPath));
        var prefixPath = Path.TrimEndingDirectorySeparator(presetDirectory) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            throw new InvalidDataException("预设背景素材缺失或路径越界。");
        RejectReparsePath(presetDirectory, source);
        var relative = "assets/" + prefix + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source).ToLowerInvariant();
        var target = FrameTrailProjectStore.ResolveAssetPath(projectFile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return relative;
    }

    private void DeleteCustomPreset(string presetFile)
    {
        if (SurfaceDialog.Show(this, "删除这个本机预设及其预设图片？不会更改项目素材。", "删除预设", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var root = Path.GetFullPath(PresetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var folder = Path.GetFullPath(Path.GetDirectoryName(presetFile)!);
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || folder == Path.TrimEndingDirectorySeparator(PresetRoot)) throw new InvalidDataException("预设目录不在 DogSC 本机预设路径内。");
            RejectReparsePath(PresetRoot, presetFile);
            Directory.Delete(folder, true); Status.Text = "已删除本机预设。";
        }
        catch (Exception ex) { SurfaceDialog.Show(this, "删除预设失败：" + ex.Message, "场景预设", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void RejectReparsePath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("素材根目录是链接或重解析点，已停止访问。");
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("素材路径越出所属目录。");
        var current = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("拒绝读取经过链接或重解析点的预设素材。");
        }
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        AddMenu(menu, "设置…", () => ShowUserPreferences());
        AddMenu(menu, "录制与权限帮助", () => ShowPermissionHelp(false));
        menu.Items.Add(new Separator());
        AddMenu(menu, "关于 DogSC", ShowAbout);
        AddMenu(menu, "版本与手动更新说明", () => OpenReleaseNotes());
        AddMenu(menu, "源码与许可证", () => OpenBundledLicenses());
        AddMenu(menu, "反馈与问题报告", () => SurfaceDialog.Show(this,
            "这是 DogSC 的 Windows 社区移植版，当前没有配置公开反馈服务。请通过获取此版本的发行渠道联系 Windows 移植维护者；Mac 原版的问题入口不处理本移植版问题。\n\n反馈时建议附上 DogSC 版本、Windows 版本、复现步骤和相关日志；请勿附带未经同意的录屏或项目素材。",
            "反馈与问题报告", MessageBoxButton.OK, MessageBoxImage.Information));
        AddMenu(menu, "键盘快捷键", ShowShortcutCheatsheet);
        AddMenu(menu, "打开本地项目目录", () => OpenLocalPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail")));
        AddMenu(menu, "打开本地设置与缓存", () => OpenLocalPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail")));
        menu.Items.Add(new Separator());
        AddMenu(menu, "口播提词器", () => ShowMemoForDesktop());
        AddMenu(menu, "退出 DogSC", RequestDesktopExitAsync);
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void ShowAbout() => CreateAboutDialog().ShowDialog();

    internal Window CreateAboutDialog(bool attachOwner = true)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var versionText = version is null ? "未知" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        var content = new StackPanel { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
        content.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        var logo = new Image { Width = 88, Height = 88, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) };
        logo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/DogSC;component/Assets/DogSCLogo.png"));
        content.Children.Add(logo);
        content.Children.Add(new TextBlock { Text = $"DogSC {versionText}", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Windows 社区移植版 · 非官方 Windows 发行版", Margin = new Thickness(0, 8, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "原作者：神烦老狗", HorizontalAlignment = HorizontalAlignment.Center });
        var source = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var link = new Hyperlink(new Run("DogSC 原仓库")) { NavigateUri = new Uri("https://github.com/laogou717/dogsc") };
        var linkStyle = new Style(typeof(Hyperlink));
        linkStyle.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("Ink")));
        linkStyle.Setters.Add(new Setter(TextElement.BackgroundProperty, new DynamicResourceExtension("Chrome")));
        linkStyle.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.Medium));
        var keyboardFocus = new Trigger { Property = ContentElement.IsFocusedProperty, Value = true };
        keyboardFocus.Setters.Add(new Setter(TextElement.BackgroundProperty, new DynamicResourceExtension("Selection")));
        keyboardFocus.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.SemiBold));
        linkStyle.Triggers.Add(keyboardFocus);
        link.Style = linkStyle;
        link.TextDecorations = TextDecorations.Underline;
        link.RequestNavigate += (_, args) => Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        source.Inlines.Add(link);
        content.Children.Add(source);
        content.Children.Add(new TextBlock { Text = "原仓库采用 MIT 许可证；Windows 移植由 lantiansb 与社区贡献者维护。", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 360 });
        var windowsSource = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var windowsLink = new Hyperlink(new Run("Windows 社区仓库 · 下载与反馈")) { NavigateUri = new Uri("https://github.com/lantiansb/dogsc"), Style = linkStyle };
        windowsLink.RequestNavigate += (_, args) => Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        windowsSource.Inlines.Add(windowsLink);
        content.Children.Add(windowsSource);
        content.Children.Add(new TextBlock { Text = "使用 FFmpeg LGPLv3 动态库与独立 GPL 编码程序。对应源码和许可证随发行包提供。", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0), MaxWidth = 360, FontSize = 12 });
        var dialog = new Window { Title = "关于 DogSC", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        SurfaceWindow.Configure(dialog, attachOwner ? this : null);
        dialog.Content = SurfaceWindow.WrapContent(dialog, content, "关于 DogSC");
        return dialog;
    }

    private void OpenReleaseNotes()
    {
        Process.Start(new ProcessStartInfo("https://github.com/lantiansb/dogsc/releases") { UseShellExecute = true });
    }

    private void OpenBundledLicenses()
    {
        var licenses = BundledResourceLocator.FindDirectory(AppContext.BaseDirectory, "licenses", "DogSC-MIT.txt");
        if (licenses is not null)
        {
            Process.Start(new ProcessStartInfo(licenses) { UseShellExecute = true });
            return;
        }

        SurfaceDialog.Show(this,
            "此构建没有找到随包提供的许可证文件。请从获取 DogSC 的原发行渠道索取对应许可证材料。",
            "源码与许可证", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed record PreviewQualityChoice(int Width, string Label);

    internal sealed class UserPreferences
    {
        /// <summary>"system" / "light" / "dark"; null means a fresh install (dark).</summary>
        public string? Appearance { get; set; }
        /// <summary>Folder for exported videos; null/empty means the default (Videos\DogSC\导出).</summary>
        public string? ExportFolder { get; set; }
        /// <summary>Last export choices (0 = original resolution, 1 = 1080p, 2 = 1440p, 3 = 4K).</summary>
        public int ExportResolutionIndex { get; set; }
        public int ExportFrameRate { get; set; } = 60;
        public bool ExportHardware { get; set; } = true;
        public bool ExportCompletionSound { get; set; } = true;
        /// <summary>Editor window geometry in DIPs (restored at launch when still on a visible screen).</summary>
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        public string? RecordingFolder { get; set; }
        public int FrameRate { get; set; } = 60;
        public int PreviewResolution { get; set; }
        public int PreviewSizingVersion { get; set; }
        public int PreviewFrameRate { get; set; } = 60;
        public bool ExcludeEditorDuringRecording { get; set; } = false;
        // Mac: automatic zoom after recording is a setting (default on). Read by CompleteRecording.
        public bool AutoZoomAfterRecording { get; set; } = true;
        public bool OriginalResolution { get; set; } = true;
        public bool RecordSystemSound { get; set; } = true;
        public bool RecordMicrophone { get; set; }
        public bool RecordCamera { get; set; }
        public string? MicrophoneId { get; set; }
        public string? CameraId { get; set; }
        /// <summary>Legacy bool from older builds; read once to migrate into Appearance and never written again.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? DarkTheme { get; set; }
        public bool FirstRunHelpShown { get; set; }
        /// <summary>录制起始条的范围模式：0 显示器 / 1 窗口 / 2 区域（Mac recorder.capture-mode）。</summary>
        public int CaptureMode { get; set; }
        /// <summary>Mac AppPreferences.rememberedZoomCreationScale: scale of the last zoom the user deliberately changed (null = 1.6).</summary>
        public double? ZoomCreationScale { get; set; }
    }

    private static string PreferencesFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "settings.json");

    /// <summary>在 WindowLoaded 完成设备枚举后调用，使用户设置在新启动时生效。</summary>
    private void ApplyUserPreferencesOnStartup(bool showFirstRunHelp = true)
    {
        var settings = ReadUserPreferences();
        ApplyUserPreferences(settings);
        if (showFirstRunHelp && !settings.FirstRunHelpShown)
        {
            ShowPermissionHelp(true);
            settings.FirstRunHelpShown = true;
            SaveUserPreferences(settings);
        }
    }

    internal static UserPreferences ReadUserPreferences()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(PreferencesFile)) ?? new();
            // Upgrade the old default, retaining deliberately lower/higher fixed widths.
            if (settings.PreviewSizingVersion == 0 && settings.PreviewResolution == 1440) settings.PreviewResolution = 0;
            settings.PreviewSizingVersion = 1;
            // Old DarkTheme=true/false becomes an explicit dark/light choice; no stored value means the dark default.
            if (settings.Appearance is null && settings.DarkTheme is { } legacyDark) settings.Appearance = legacyDark ? AppTheme.Dark : AppTheme.Light;
            settings.Appearance = AppTheme.Normalize(settings.Appearance);
            settings.DarkTheme = null;
            return settings;
        }
        catch { return new() { Appearance = AppTheme.Dark }; }
    }

    internal static void SaveUserPreferences(UserPreferences settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)!);
        var temp = PreferencesFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, PreferencesFile, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void ApplyUserPreferences(UserPreferences settings)
    {
        preferredRecordingRoot = !string.IsNullOrWhiteSpace(settings.RecordingFolder) && Directory.Exists(settings.RecordingFolder)
            ? Path.GetFullPath(settings.RecordingFolder) : null;
        CaptureFps.SelectedIndex = settings.FrameRate == 30 ? 0 : 1;
        previewResolution = settings.PreviewResolution is 0 or 960 or 1440 or 1920 ? settings.PreviewResolution : 0;
        previewFrameRate = settings.PreviewFrameRate == 60 ? 60 : 30;
        excludeEditorDuringRecording = settings.ExcludeEditorDuringRecording;
        CaptureResolution.SelectedIndex = settings.OriginalResolution ? 1 : 0;
        SystemSound.IsChecked = settings.RecordSystemSound;
        Microphone.IsChecked = settings.RecordMicrophone;
        RecordCamera.IsChecked = settings.RecordCamera;
        SelectDevice(MicDevices, settings.MicrophoneId);
        SelectDevice(CameraDevices, settings.CameraId);
        // Tolerates preference objects that only carry the legacy DarkTheme flag.
        var appearance = settings.Appearance ?? (settings.DarkTheme is { } legacyDark ? (legacyDark ? AppTheme.Dark : AppTheme.Light) : AppTheme.Dark);
        if (!AppTheme.Initialized || AppTheme.Mode != AppTheme.Normalize(appearance) || darkTheme != AppTheme.IsDark) ApplyAppearance(appearance, persist: false);
        if (project is not null)
        {
            if (playing) StopPlayback();
            _ = Guard(() => RenderPreview(Playhead.Value));
        }
    }

    private static void SelectDevice(ComboBox combo, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || combo.ItemsSource is not System.Collections.IEnumerable items) return;
        foreach (var item in items)
            if (item is AudioDeviceInfo audio && string.Equals(audio.Id, id, StringComparison.Ordinal)) { combo.SelectedItem = item; return; }
            else if (item is CameraInfo camera && string.Equals(camera.Id, id, StringComparison.Ordinal)) { combo.SelectedItem = item; return; }
    }

    private void ShowUserPreferences()
    {
        CreateUserPreferencesDialog(DesktopDialogOwner()).ShowDialog();
    }

    // Mac AppSettingsView: every control takes effect (and is stored) immediately; "完成" only closes the window.
    // Each change re-reads settings.json, changes one field and writes it back, so fields owned by other
    // features (recording mode, auto-zoom, window geometry…) are never overwritten by this dialog.
    private Window CreateUserPreferencesDialog(Window? owner = null)
    {
        var current = ReadUserPreferences();
        var loading = true;
        var root = new DockPanel { Margin = new Thickness(18, 12, 18, 14), LastChildFill = true };
        var actions = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("Hairline"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        var actionRow = new DockPanel { LastChildFill = false };
        const string defaultActionStatus = "更改会立即生效";
        var actionStatus = new TextBlock
        {
            Text = defaultActionStatus,
            Foreground = (System.Windows.Media.Brush)FindResource("Secondary"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 300,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(actionStatus, System.Windows.Automation.AutomationLiveSetting.Assertive);
        void SetActionStatus(string text)
        {
            actionStatus.Text = text;
            actionStatus.ToolTip = text == defaultActionStatus ? null : text;
        }
        void ClearActionStatus() => SetActionStatus(defaultActionStatus);
        actionRow.Children.Add(actionStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "完成", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), Style = TryFindResource("PrimaryButton") as Style };
        System.Windows.Automation.AutomationProperties.SetAutomationId(save, "UserPreferencesSave");
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        actionRow.Children.Add(buttons);
        actions.Child = actionRow;
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

        // Writes one change immediately and applies it to the running app.
        bool Persist(Action<UserPreferences> change)
        {
            if (loading) return true;
            try
            {
                var fresh = ReadUserPreferences();
                change(fresh);
                fresh.MicrophoneId = (MicDevices.SelectedItem as AudioDeviceInfo)?.Id ?? fresh.MicrophoneId;
                fresh.CameraId = (CameraDevices.SelectedItem as CameraInfo)?.Id ?? fresh.CameraId;
                SaveUserPreferences(fresh);
                try { ApplyUserPreferences(fresh); }
                catch (Exception ex)
                {
                    App.Log(ex);
                    SetActionStatus("设置已保存，部分内容未能立即应用。");
                    return true;
                }
                ClearActionStatus();
                return true;
            }
            catch (Exception ex)
            {
                App.Log(ex);
                SetActionStatus("设置未能保存，请检查应用数据权限。");
                return false;
            }
        }

        var content = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        Border Section(string title, UIElement body)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            stack.Children.Add(body);
            return new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Surface"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("Hairline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack
            };
        }

        TextBlock FieldLabel(string text) => new()
        {
            Text = text,
            Foreground = (System.Windows.Media.Brush)FindResource("Secondary"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };

        TextBlock FieldError()
        {
            var error = new TextBlock
            {
                FontSize = 11,
                Height = 17,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Assertive);
            if (Application.Current?.TryFindResource("TransientPreferences") is TransientPreferences highContrast)
            {
                void UpdateErrorForeground()
                {
                    error.SetResourceReference(TextBlock.ForegroundProperty, highContrast.HighContrast
                        ? SystemColors.WindowTextBrushKey
                        : "Ink");
                }
                UpdateErrorForeground();
                System.ComponentModel.PropertyChangedEventManager.AddHandler(highContrast, (_, _) => UpdateErrorForeground(), nameof(TransientPreferences.HighContrast));
            }
            else error.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return error;
        }

        Grid FieldGrid(params (string Label, FrameworkElement Control)[] fields)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < fields.Length; i++)
            {
                var column = i == 0 ? 0 : 2;
                var label = FieldLabel(fields[i].Label);
                Grid.SetColumn(label, column);
                grid.Children.Add(label);
                Grid.SetRow(fields[i].Control, 1);
                Grid.SetColumn(fields[i].Control, column);
                grid.Children.Add(fields[i].Control);
            }
            return grid;
        }

        // ---- recording folder: validated, then stored (Enter, leaving the box, browsing or 完成)
        var folder = new TextBox { Text = current.RecordingFolder ?? preferredRecordingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail"), Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(folder, "UserPreferencesRecordingFolder");
        var folderError = FieldError();
        System.Windows.Automation.AutomationProperties.SetAutomationId(folderError, "UserPreferencesRecordingFolderError");
        var committedFolder = folder.Text.Trim();
        bool CommitFolder(bool focusOnError)
        {
            folderError.Text = "";
            var path = folder.Text.Trim();
            void Fail(string message) { folderError.Text = message; if (focusOnError) folder.Focus(); }
            if (path.Length == 0) { Fail("请填写默认录制保存目录。"); return false; }
            try { path = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
            {
                Fail("目录路径格式无效，请检查后重试。");
                return false;
            }
            try
            {
                Directory.CreateDirectory(path);
                var writeCheck = Path.Combine(path, ".frametrail-write-check-" + Guid.NewGuid().ToString("N"));
                using (var probe = new FileStream(writeCheck, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) probe.WriteByte(0);
            }
            catch (UnauthorizedAccessException) { Fail("无法写入此目录，请检查文件夹权限。"); return false; }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                Fail("无法使用此目录，请检查路径和磁盘状态。");
                return false;
            }
            if (string.Equals(path, committedFolder, StringComparison.OrdinalIgnoreCase) && current.RecordingFolder is not null) return true;
            if (!Persist(p => p.RecordingFolder = path)) return false;
            committedFolder = path;
            return true;
        }
        var browse = new Button { Content = "选择目录…", MinWidth = 92, Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择默认录制保存目录", InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            if (picker.ShowDialog(this) == true) { folder.Text = picker.FolderName; CommitFolder(false); }
        };
        folder.TextChanged += (_, _) =>
        {
            folderError.Text = "";
            ClearActionStatus();
        };
        folder.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitFolder(true); e.Handled = true; } };
        folder.LostKeyboardFocus += (_, _) => { if (!loading && folder.Text.Trim() != committedFolder) CommitFolder(false); };
        var folderRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);

        var frameRate = new ComboBox { ItemsSource = new[] { 30, 60 }, SelectedItem = current.FrameRate == 30 ? 30 : 60, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        var resolution = new ComboBox { ItemsSource = new[] { "最高 1080p", "原始分辨率" }, SelectedIndex = current.OriginalResolution ? 1 : 0, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        frameRate.SelectionChanged += (_, _) => { if (int.TryParse(Convert.ToString(frameRate.SelectedItem), out var fps) && fps is 30 or 60) Persist(p => p.FrameRate = fps); };
        resolution.SelectionChanged += (_, _) => Persist(p => p.OriginalResolution = resolution.SelectedIndex == 1);
        var recording = new StackPanel();
        recording.Children.Add(FieldLabel("保存目录"));
        recording.Children.Add(folderRow);
        recording.Children.Add(folderError);
        recording.Children.Add(FieldGrid(("录制帧率", frameRate), ("录制分辨率", resolution)));
        // Mac: recording ends with automatic zoom clips planned from the pointer; this switch turns that off.
        var autoZoom = new CheckBox { Content = "录制结束后自动生成缩放", IsChecked = current.AutoZoomAfterRecording, ToolTip = "录制结束进入编辑器时，根据鼠标点击自动生成缩放片段；关闭后保持画面不缩放。", Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 10, 0, 2), MinHeight = 30 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(autoZoom, "UserPreferencesAutoZoom");
        autoZoom.Checked += (_, _) => Persist(p => p.AutoZoomAfterRecording = true);
        autoZoom.Unchecked += (_, _) => Persist(p => p.AutoZoomAfterRecording = false);
        recording.Children.Add(autoZoom);
        content.Children.Add(Section("录制", recording));

        var system = new CheckBox { Content = "录制系统声音", IsChecked = current.RecordSystemSound, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 1, 0, 4), MinHeight = 30 };
        var microphone = new CheckBox { Content = "录制麦克风", IsChecked = current.RecordMicrophone, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 0, 0, 4), MinHeight = 30 };
        var camera = new CheckBox { Content = "录制摄像头", IsChecked = current.RecordCamera, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 0, 0, 1), MinHeight = 30 };
        var sources = new StackPanel();
        sources.Children.Add(system);
        sources.Children.Add(microphone);
        sources.Children.Add(camera);
        void OnSourceToggled() => Persist(p => { p.RecordSystemSound = system.IsChecked == true; p.RecordMicrophone = microphone.IsChecked == true; p.RecordCamera = camera.IsChecked == true; });
        foreach (var toggle in new[] { system, microphone, camera })
        {
            toggle.Checked += (_, _) => OnSourceToggled();
            toggle.Unchecked += (_, _) => OnSourceToggled();
        }
        content.Children.Add(Section("音视频来源", sources));

        // ---- export
        var exportFolderBox = new TextBox { Text = ExportLocation.Resolve(current.ExportFolder), Height = 36, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(exportFolderBox, "UserPreferencesExportFolder");
        System.Windows.Automation.AutomationProperties.SetName(exportFolderBox, "导出位置");
        var exportBrowse = new Button { Content = "选择目录…", MinWidth = 92, Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        exportBrowse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择导出文件夹", InitialDirectory = Directory.Exists(exportFolderBox.Text) ? exportFolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            if (picker.ShowDialog(this) != true) return;
            exportFolderBox.Text = picker.FolderName;
            Persist(p => p.ExportFolder = picker.FolderName);
        };
        var exportReset = new Button { Content = "恢复默认", MinWidth = 80, Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        exportReset.Click += (_, _) =>
        {
            if (!Persist(p => p.ExportFolder = null)) return;
            exportFolderBox.Text = ExportLocation.Resolve(null);
        };
        var exportRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(exportReset, Dock.Right);
        DockPanel.SetDock(exportBrowse, Dock.Right);
        exportRow.Children.Add(exportReset);
        exportRow.Children.Add(exportBrowse);
        exportRow.Children.Add(exportFolderBox);
        var exportSound = new CheckBox { Content = "导出完成后播放提示音", IsChecked = current.ExportCompletionSound, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 10, 0, 1), MinHeight = 30 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(exportSound, "UserPreferencesExportSound");
        exportSound.Checked += (_, _) => Persist(p => p.ExportCompletionSound = true);
        exportSound.Unchecked += (_, _) => Persist(p => p.ExportCompletionSound = false);
        var exportSection = new StackPanel();
        exportSection.Children.Add(FieldLabel("导出位置"));
        exportSection.Children.Add(exportRow);
        exportSection.Children.Add(exportSound);
        content.Children.Add(Section("导出", exportSection));

        // ---- appearance (system / light / dark) and preview
        var previewWidth = new ComboBox { ItemsSource = new[] { new PreviewQualityChoice(0, "自动 · 清晰显示"), new PreviewQualityChoice(960, "960 px"), new PreviewQualityChoice(1440, "1440 px"), new PreviewQualityChoice(1920, "1920 px") }, DisplayMemberPath = "Label", SelectedValuePath = "Width", SelectedValue = current.PreviewResolution is 0 or 960 or 1440 or 1920 ? current.PreviewResolution : 0, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        var previewFps = new ComboBox { ItemsSource = new[] { 30, 60 }, SelectedItem = current.PreviewFrameRate == 60 ? 60 : 30, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        void OnPreviewChanged()
        {
            if (previewWidth.SelectedValue is int width && width is 0 or 960 or 1440 or 1920 && previewFps.SelectedItem is int rate && rate is 30 or 60)
                Persist(p => { p.PreviewResolution = width; p.PreviewSizingVersion = 1; p.PreviewFrameRate = rate; });
        }
        previewWidth.SelectionChanged += (_, _) => OnPreviewChanged();
        previewFps.SelectionChanged += (_, _) => OnPreviewChanged();
        System.Windows.Automation.AutomationProperties.SetName(previewWidth, "预览分辨率");
        System.Windows.Automation.AutomationProperties.SetName(previewFps, "预览帧率");
        var appearance = new StackPanel();
        appearance.Children.Add(FieldLabel("外观"));
        var appearanceButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Height = 40, Margin = new Thickness(0, 0, 0, 8) };
        var appearanceChoices = new List<(string Mode, Button Button)>();
        var selectedAppearance = AppTheme.Normalize(current.Appearance);
        void RefreshAppearanceButtons()
        {
            foreach (var (mode, button) in appearanceChoices)
            {
                var selected = mode == selectedAppearance;
                button.SetResourceReference(BackgroundProperty, selected ? "Selection" : "ControlSurface");
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已选择" : "未选择");
            }
        }
        foreach (var (mode, label) in new[] { (AppTheme.FollowSystem, "跟随系统"), (AppTheme.Light, "浅色"), (AppTheme.Dark, "深色") })
        {
            var button = new Button { Content = label, Height = 36, Margin = new Thickness(2) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "UserPreferencesAppearance_" + mode);
            System.Windows.Automation.AutomationProperties.SetName(button, "外观：" + label);
            var chosen = mode;
            button.Click += (_, _) =>
            {
                selectedAppearance = chosen;
                RefreshAppearanceButtons();
                Persist(p => { p.Appearance = chosen; p.DarkTheme = null; });
            };
            appearanceChoices.Add((mode, button));
            appearanceButtons.Children.Add(button);
        }
        RefreshAppearanceButtons();
        appearance.Children.Add(appearanceButtons);
        appearance.Children.Add(FieldGrid(("预览清晰度", previewWidth), ("预览帧率", previewFps)));
        var excludeEditor = new CheckBox { Content = "录制时排除编辑器窗口", IsChecked = current.ExcludeEditorDuringRecording, ToolTip = "录制显示器时避免把 DogSC 主窗口录进画面；关闭后可录制本程序自身。", Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 10, 0, 4), MinHeight = 30 };
        excludeEditor.Checked += (_, _) => Persist(p => p.ExcludeEditorDuringRecording = true);
        excludeEditor.Unchecked += (_, _) => Persist(p => p.ExcludeEditorDuringRecording = false);
        appearance.Children.Add(excludeEditor);
        appearance.Children.Add(new TextBlock
        {
            Text = "自动模式匹配窗口的实际像素，放大画面时保留源视频细节。固定较小尺寸可减少预览开销。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("Secondary"),
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0)
        });
        content.Children.Add(Section("编辑外观与预览", appearance));

        // ---- help
        var replayHelp = new Button { Content = "重新显示首次使用说明", HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 34, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(replayHelp, "UserPreferencesReplayHelp");
        replayHelp.Click += (_, _) => ShowPermissionHelp(true);
        content.Children.Add(Section("帮助", replayHelp));

        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var dialog = new Window { Title = "设置", Width = 560, Height = 760, ShowInTaskbar = false };
        SurfaceWindow.Configure(dialog, owner);
        dialog.Content = SurfaceWindow.WrapContent(dialog, root, "设置");
        save.Click += (_, _) =>
        {
            if (!CommitFolder(true)) return;
            dialog.DialogResult = true;
        };
        loading = false;
        return dialog;
    }

    private void ShowPermissionHelp(bool firstRun)
    {
        var root = new DockPanel { Margin = new Thickness(24) };
        var close = new Button { Content = "我知道了", IsDefault = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Style = TryFindResource("PrimaryButton") as Style };
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var text = new TextBlock
        {
            Text = "首次录制前，请检查以下项目：\n\n• 屏幕：选择显示器、窗口或区域。Windows 可能会显示系统捕获提示；请在系统提示中允许本次捕获。\n\n• 麦克风与摄像头：在录制面板选择设备。若设备不可用，请到 Windows“设置 → 隐私和安全性 → 麦克风 / 摄像头”，允许桌面应用访问。\n\n• 系统声音：启用“系统声音”后将录制默认播放设备的回环音频。\n\n• 保存位置：确认目标磁盘有足够空间。录制保护会在主进程私有内存超过 3 GiB、采集子进程超过 2 GiB 或剩余磁盘空间低于 300 MiB 时停止录制；素材保留在项目目录。\n\n这些说明不会授予系统权限；权限仍由 Windows 控制。",
            TextWrapping = TextWrapping.Wrap, LineHeight = 22
        };
        root.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var helpTitle = firstRun ? "录制权限与准备" : "录制与权限帮助";
        var dialog = new Window { Title = helpTitle, Width = 560, Height = 532, MinWidth = 480, MinHeight = 422, ShowInTaskbar = false };
        SurfaceWindow.Configure(dialog, DesktopDialogOwner(), resizable: true);
        dialog.Content = SurfaceWindow.WrapContent(dialog, root, helpTitle);
        close.Click += (_, _) => dialog.DialogResult = true;
        dialog.ShowDialog();
    }

    private static void OpenLocalPath(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
}

/// <summary>本机最近项目记录，损坏或已移除的路径会在读取时过滤。</summary>
public static class RecentProjects
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "recent-projects.json");
    public static IReadOnlyList<string> Read()
    {
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            return paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        }
        catch { return []; }
    }
    public static void Add(string path)
    {
        var diagnosticFlags = new[] { "--visual-states", "--editor-smoke", "--export-panel-smoke", "--recovery-smoke", "--playback-smoke", "--smoke", "--workflow-smoke" };
        if (Environment.GetCommandLineArgs().Any(arg => diagnosticFlags.Contains(arg, StringComparer.OrdinalIgnoreCase))) return;
        try
        {
            var full = Path.GetFullPath(path); var list = Read().Where(x => !string.Equals(x, full, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, full); Write(list);
        }
        // A failing recent-list write must never make a successful "open project" look like a failure.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { App.Log(ex); }
    }
    /// <summary>Drops a project from the list (deleted or moved to the recycle bin).</summary>
    public static void Remove(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = Read().Where(x => !string.Equals(x, full, StringComparison.OrdinalIgnoreCase)).ToList();
            Write(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { App.Log(ex); }
    }
    private static void Write(IEnumerable<string> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(12), new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Mac recentProjectSummaries: the project's own title and date, falling back to the folder name.</summary>
    public static (string Title, string DateText) Describe(string path)
    {
        var title = ReadDisplayName(path);
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileName(Path.GetDirectoryName(path)) ?? Path.GetFileNameWithoutExtension(path);
        string date;
        try { date = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); } catch { date = ""; }
        return (title, date);
    }

    // Streams the top-level "displayName" only, so a project with a long pointer track is not fully deserialized.
    private static string? ReadDisplayName(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var match = reader.ValueTextEquals("displayName") || reader.ValueTextEquals("DisplayName");
                if (!reader.Read()) return null;
                if (match) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
        }
        catch { }
        return null;
    }
}
