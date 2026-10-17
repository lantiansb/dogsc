using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        var recent = new MenuItem { Header = "最近项目" };
        foreach (var path in RecentProjects.Read())
        {
            var name = RecentProjects.DisplayNameFor(path);
            var heading = new StackPanel { Width = 340, Margin = new Thickness(0, 2, 0, 2) };
            heading.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            heading.Children.Add(new TextBlock
            {
                Text = Path.GetDirectoryName(path), FontSize = 11, Opacity = .72,
                Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var item = new MenuItem { Header = heading, ToolTip = path };
            System.Windows.Automation.AutomationProperties.SetName(item, name + "  ·  " + path);
            item.Click += async (_, _) => await Guard(async () => { await OpenProject(path); RecentProjects.Add(path); });
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

    private async Task PickProject()
    {
        var dialog = new OpenFileDialog { Title = "打开 DogSC 项目", Filter = "DogSC 项目|*.frametrail|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await Guard(async () => { await OpenProject(dialog.FileName); RecentProjects.Add(dialog.FileName); });
    }

    private async Task ImportVideoFromMenu()
    {
        var pick = new OpenFileDialog { Title = "导入视频并创建项目", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm", CheckFileExists = true };
        if (pick.ShowDialog(this) != true) return;
        await Guard(async () =>
        {
            var info = await MediaTools.ProbeAsync(pick.FileName);
            if (info.Width <= 0 || info.Duration <= 0) throw new InvalidDataException("文件没有可用视频。");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail", "Import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(Path.Combine(dir, "media"));
            var relative = "media/source" + Path.GetExtension(pick.FileName).ToLowerInvariant();
            File.Copy(pick.FileName, Path.Combine(dir, relative));
            var imported = MakeProject(relative, info); imported.DisplayName = Path.GetFileNameWithoutExtension(pick.FileName);
            var file = Path.Combine(dir, "project.frametrail"); FrameTrailProjectStore.Save(file, imported);
            await OpenProject(file); RecentProjects.Add(file);
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
            RecentProjects.Add(imported.ProjectFilePath);
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

    private void RenameProjectClick(object sender, RoutedEventArgs e)
    {
        if (project is null || projectFile is null) { Status.Text = "请先打开或录制一个项目。"; return; }
        var current = project.DisplayName ?? Path.GetFileName(Path.GetDirectoryName(projectFile)) ?? "未命名项目";
        var name = PromptText(this, "重命名项目", "项目名称", current);
        if (name is null) return;
        name = name.Trim();
        if (name.Length is 0 or > 240) { SurfaceDialog.Show(this, "项目名称长度应为 1–240 个字符。", "无法重命名", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try { project.DisplayName = name; SaveProject(); ProjectTitle.Text = name; }
        catch (Exception ex) { SurfaceDialog.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DeleteProjectClick(object sender, RoutedEventArgs e)
    {
        if (project is null || projectFile is null) { Status.Text = "当前没有打开的项目。"; return; }
        var answer = SurfaceDialog.Show(this, "只会把当前 .frametrail 项目文件移入回收站。项目文件夹中的原始视频、音频和其他素材都会保留。是否继续？", "移除项目文件", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            SaveProject();
            FileSystem.DeleteFile(projectFile, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            project = null; projectFile = null; history = null; timelineAssets = null; selectedEffect = null;
            Clips.ItemsSource = null; Markers.ItemsSource = null; TimelineView.Project = null; TimelineView.Refresh();
            EmptyState.Visibility = Visibility.Visible; ProjectTitle.Text = "未命名项目"; TimelineInfo.Text = ""; Status.Text = "项目文件已移入回收站，原始素材仍保留在磁盘。";
            BuildInspector();
        }
        catch (Exception ex) { SurfaceDialog.Show(this, "移除失败：" + ex.Message, "项目文件未移动", MessageBoxButton.OK, MessageBoxImage.Error); }
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
                parent.Click += (_, _) => { if (project?.Timeline.MapSourceToOutput(marker.SourceTime) is { } t) { Playhead.Value = t; _ = Guard(() => RenderPreview(t)); } else Status.Text = "此标记位于已剪除的素材区间，当前时间线无法跳转。"; };
                var delete = new MenuItem { Header = "删除此标记" }; delete.Click += (_, _) => ApplyEdit(p => p.Markers.RemoveAll(x => x.Id == marker.Id));
                parent.Items.Add(delete); menu.Items.Add(parent);
            }
        }
        if (project is null) menu.Items.Insert(0, new MenuItem { Header = "请先打开项目", IsEnabled = false });
        menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true;
    }

    private void AddMarkerAtPlayhead()
    {
        if (project is null) { Status.Text = "请先打开项目。"; return; }
        var label = PromptText(this, "添加标记", "标记名称", $"标记 {project.Markers.Count + 1}");
        if (label is null) return;
        ApplyEdit(p => p.Markers.Add(new RecordingMarker(Guid.NewGuid(), SourceNow, label.Trim().Length == 0 ? "标记" : label.Trim())));
    }

    private void AspectClick(object sender, RoutedEventArgs e)
    {
        if (project is null) { Status.Text = "请先打开项目。"; return; }
        var menu = NewMenu();
        foreach (var item in new[] { ("16:9", 16, 9), ("4:3", 4, 3), ("9:16", 9, 16), ("1:1", 1, 1), ("3:4", 3, 4), ("2.39:1", 239, 100) })
        {
            var entry = new MenuItem { Header = item.Item1, IsCheckable = true, IsChecked = (long)project.Settings.CanvasWidth * item.Item3 == (long)project.Settings.CanvasHeight * item.Item2 };
            entry.Click += (_, _) => SetAspect(item.Item2, item.Item3); menu.Items.Add(entry);
        }
        var custom = new MenuItem { Header = "自定义比例…" }; custom.Click += (_, _) =>
        {
            var value = PromptText(this, "自定义画布比例", "输入宽:高，例如 1920:1080", $"{project.Settings.CanvasWidth}:{project.Settings.CanvasHeight}");
            if (value is null) return;
            var parts = value.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w is < 2 or > 7680 || h is < 2 or > 4320) { SurfaceDialog.Show(this, "请输入范围内的整数比例，例如 1920:1080。", "比例无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            SetAspect(w, h);
        };
        menu.Items.Add(new Separator()); menu.Items.Add(custom); menu.PlacementTarget = (UIElement)sender; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void SetAspect(int width, int height)
    {
        ApplyEdit(p =>
        {
            p.Settings.CanvasWidth = width; p.Settings.CanvasHeight = height;
            p.Settings.Canvas ??= new CanvasSettings(); p.Settings.Canvas.AspectWidth = width; p.Settings.Canvas.AspectHeight = height;
        });
        var gcd = Gcd(width, height); AspectLabel.Text = $"{width / gcd}:{height / gcd}";
    }

    private void AddElementClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu();
        AddMenu(menu, "添加运镜片段", () => AddMotionClip());
        AddMenu(menu, "添加贴图…", () => AddOverlayClick(this, new RoutedEventArgs()));
        AddMenu(menu, "添加模糊遮罩", () => AddBlurClip());
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
            var item = new MenuItem { Header = entry.Name };
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
        if (project is null) { Status.Text = "请先打开项目。"; return; }
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
        if (project is null || projectFile is null) { Status.Text = "请先打开项目。"; return; }
        var name = PromptText(this, "保存场景预设", "预设名称", project.DisplayName ?? "我的样式");
        if (name is null) return; name = name.Trim();
        if (name.Length is 0 or > 100) { SurfaceDialog.Show(this, "预设名称长度应为 1–100 个字符。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
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
        if (project is null || projectFile is null) { Status.Text = "请先打开项目。"; return; }
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
        if (project is null || projectFile is null) { Status.Text = "请先打开项目。"; return; }
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
            ApplyEdit(p => p.Settings = settings); Status.Text = "已应用本机预设：" + preset.Name;
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
        AddMenu(menu, "录制与界面设置…", () => ShowUserPreferences());
        AddMenu(menu, "录制与权限帮助", () => ShowPermissionHelp(false));
        menu.Items.Add(new Separator());
        AddMenu(menu, "关于 DogSC", ShowAbout);
        AddMenu(menu, "版本与手动更新说明", () => OpenReleaseNotes());
        AddMenu(menu, "源码与许可证", () => OpenBundledLicenses());
        AddMenu(menu, "反馈与问题报告", () => SurfaceDialog.Show(this,
            "这是 DogSC 的 Windows 社区移植版，当前没有配置公开反馈服务。请通过获取此版本的发行渠道联系 Windows 移植维护者；Mac 原版的问题入口不处理本移植版问题。\n\n反馈时建议附上 DogSC 版本、Windows 版本、复现步骤和相关日志；请勿附带未经同意的录屏或项目素材。",
            "反馈与问题报告", MessageBoxButton.OK, MessageBoxImage.Information));
        AddMenu(menu, "键盘快捷键", () => SurfaceDialog.Show(this, "Ctrl+S　保存项目\nCtrl+Z　撤销\nCtrl+Y　重做\nSpace　播放或暂停\n← / →　移动1帧；Shift+← / →　移动5帧\nS　在播放头分割所选效果；未选效果时分割播放头所在主片段\nQ / W　裁去所选效果的起点/终点；未选效果时波纹删除主片段的前段/后段\nD / Delete　删除所选项\nI / O　设置导出范围起点/终点\nCtrl+C / Ctrl+V　复制/粘贴效果；Ctrl+Shift+V　粘贴属性\n" + RecordingMarkerShortcutHelp + "\n\n文本框、下拉框或滑块获得焦点时，编辑快捷键暂停。", "键盘快捷键", MessageBoxButton.OK, MessageBoxImage.Information));
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

    private sealed class UserPreferences
    {
        public string? RecordingFolder { get; set; }
        public int FrameRate { get; set; } = 60;
        public int PreviewResolution { get; set; }
        public int PreviewSizingVersion { get; set; }
        public int PreviewFrameRate { get; set; } = 60;
        public bool ExcludeEditorDuringRecording { get; set; } = true;
        public bool OriginalResolution { get; set; } = true;
        public bool RecordSystemSound { get; set; } = true;
        public bool RecordMicrophone { get; set; }
        public bool RecordCamera { get; set; }
        public string? MicrophoneId { get; set; }
        public string? CameraId { get; set; }
        public bool DarkTheme { get; set; }
        public bool FirstRunHelpShown { get; set; }
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

    private static UserPreferences ReadUserPreferences()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(PreferencesFile)) ?? new();
            // Upgrade the old default, retaining deliberately lower/higher fixed widths.
            if (settings.PreviewSizingVersion == 0 && settings.PreviewResolution == 1440) settings.PreviewResolution = 0;
            settings.PreviewSizingVersion = 1;
            return settings;
        }
        catch { return new(); }
    }

    private static void SaveUserPreferences(UserPreferences settings)
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
        RefreshRecorderSavePathDisplay();
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
        if (darkTheme != settings.DarkTheme) ThemeClick(this, new RoutedEventArgs());
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

    private Window CreateUserPreferencesDialog(Window? owner = null)
    {
        var current = ReadUserPreferences();
        var root = new DockPanel { Margin = new Thickness(18, 12, 18, 14), LastChildFill = true };
        var actions = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("Hairline"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        var actionRow = new DockPanel { LastChildFill = false };
        var actionStatus = new TextBlock
        {
            Text = "更改仅在保存后生效",
            Foreground = (System.Windows.Media.Brush)FindResource("Secondary"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        const string defaultActionStatus = "更改仅在保存后生效";
        System.Windows.Automation.AutomationProperties.SetLiveSetting(actionStatus, System.Windows.Automation.AutomationLiveSetting.Assertive);
        void SetActionStatus(string text)
        {
            actionStatus.Text = text;
            actionStatus.ToolTip = text == defaultActionStatus ? null : text;
        }
        void ClearActionStatus() => SetActionStatus(defaultActionStatus);
        actionRow.Children.Add(actionStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 82, Margin = new Thickness(4, 0, 0, 0) };
        var save = new Button { Content = "保存设置", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), Style = TryFindResource("PrimaryButton") as Style };
        System.Windows.Automation.AutomationProperties.SetAutomationId(save, "UserPreferencesSave");
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        actionRow.Children.Add(buttons);
        actions.Child = actionRow;
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);

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

        var folder = new TextBox { Text = preferredRecordingRoot ?? current.RecordingFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail"), Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(folder, "UserPreferencesRecordingFolder");
        var folderError = FieldError();
        System.Windows.Automation.AutomationProperties.SetAutomationId(folderError, "UserPreferencesRecordingFolderError");
        var browse = new Button { Content = "选择目录…", MinWidth = 92, Height = 36, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "选择默认录制保存目录", FolderName = Directory.Exists(folder.Text) ? folder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) }; if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName; };
        folder.TextChanged += (_, _) =>
        {
            folderError.Text = "";
            ClearActionStatus();
        };
        var folderRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);

        var frameRate = new ComboBox { ItemsSource = new[] { 30, 60 }, SelectedItem = current.FrameRate == 30 ? 30 : 60, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        var resolution = new ComboBox { ItemsSource = new[] { "最高 1080p", "原始分辨率" }, SelectedIndex = current.OriginalResolution ? 1 : 0, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        frameRate.SelectionChanged += (_, _) => ClearActionStatus();
        resolution.SelectionChanged += (_, _) => ClearActionStatus();
        var recording = new StackPanel();
        recording.Children.Add(FieldLabel("保存目录"));
        recording.Children.Add(folderRow);
        recording.Children.Add(folderError);
        recording.Children.Add(FieldGrid(("录制帧率", frameRate), ("录制分辨率", resolution)));
        content.Children.Add(Section("录制", recording));

        var system = new CheckBox { Content = "录制系统声音", IsChecked = current.RecordSystemSound, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 1, 0, 4), MinHeight = 30 };
        var microphone = new CheckBox { Content = "录制麦克风", IsChecked = current.RecordMicrophone, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 0, 0, 4), MinHeight = 30 };
        var camera = new CheckBox { Content = "录制摄像头", IsChecked = current.RecordCamera, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 0, 0, 1), MinHeight = 30 };
        var sources = new StackPanel();
        sources.Children.Add(system);
        sources.Children.Add(microphone);
        sources.Children.Add(camera);
        foreach (var toggle in new[] { system, microphone, camera })
        {
            toggle.Checked += (_, _) => ClearActionStatus();
            toggle.Unchecked += (_, _) => ClearActionStatus();
        }
        content.Children.Add(Section("音视频来源", sources));

        var previewWidth = new ComboBox { ItemsSource = new[] { new PreviewQualityChoice(0, "自动 · 清晰显示"), new PreviewQualityChoice(960, "960 px"), new PreviewQualityChoice(1440, "1440 px"), new PreviewQualityChoice(1920, "1920 px") }, DisplayMemberPath = "Label", SelectedValuePath = "Width", SelectedValue = current.PreviewResolution is 0 or 960 or 1440 or 1920 ? current.PreviewResolution : 0, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        var previewFps = new ComboBox { ItemsSource = new[] { 30, 60 }, SelectedItem = current.PreviewFrameRate == 60 ? 60 : 30, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 3, 0, 3) };
        previewWidth.SelectionChanged += (_, _) => ClearActionStatus();
        previewFps.SelectionChanged += (_, _) => ClearActionStatus();
        System.Windows.Automation.AutomationProperties.SetName(previewWidth, "预览分辨率");
        System.Windows.Automation.AutomationProperties.SetName(previewFps, "预览帧率");
        var appearance = new StackPanel();
        appearance.Children.Add(FieldGrid(("预览清晰度", previewWidth), ("预览帧率", previewFps)));
        var dark = new CheckBox { Content = "使用深色编辑界面", IsChecked = current.DarkTheme, Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 10, 0, 8), MinHeight = 30 };
        var excludeEditor = new CheckBox { Content = "录制时排除编辑器窗口", IsChecked = current.ExcludeEditorDuringRecording, ToolTip = "录制显示器时避免把 DogSC 主窗口录进画面；关闭后可录制本程序自身。", Style = TryFindResource("InspectorToggle") as Style, Margin = new Thickness(0, 0, 0, 4), MinHeight = 30 };
        foreach (var toggle in new[] { dark, excludeEditor })
        {
            toggle.Checked += (_, _) => ClearActionStatus();
            toggle.Unchecked += (_, _) => ClearActionStatus();
        }
        appearance.Children.Add(dark);
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

        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var dialog = new Window { Title = "录制与界面设置", Width = 560, Height = 720, ShowInTaskbar = false };
        SurfaceWindow.Configure(dialog, owner);
        dialog.Content = SurfaceWindow.WrapContent(dialog, root, "录制与界面设置");
        save.Click += (_, _) =>
        {
            folderError.Text = "";
            ClearActionStatus();
            if (!int.TryParse(Convert.ToString(frameRate.SelectedItem), out var fps) || fps is not (30 or 60))
            {
                SetActionStatus("请选择 30 或 60 FPS。");
                frameRate.Focus();
                return;
            }
            var path = folder.Text.Trim();
            if (path.Length == 0)
            {
                folderError.Text = "请填写默认录制保存目录。";
                folder.Focus();
                return;
            }
            UserPreferences updated;
            try
            {
                try { path = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
                {
                    folderError.Text = "目录路径格式无效，请检查后重试。";
                    folder.Focus();
                    return;
                }
                try
                {
                    Directory.CreateDirectory(path);
                    var writeCheck = Path.Combine(path, ".frametrail-write-check-" + Guid.NewGuid().ToString("N"));
                    using (var probe = new FileStream(writeCheck, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) probe.WriteByte(0);
                }
                catch (UnauthorizedAccessException)
                {
                    folderError.Text = "无法写入此目录，请检查文件夹权限。";
                    folder.Focus();
                    return;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    folderError.Text = "无法使用此目录，请检查路径和磁盘状态。";
                    folder.Focus();
                    return;
                }
                if (previewWidth.SelectedValue is not int width || width is not (0 or 960 or 1440 or 1920) || previewFps.SelectedItem is not int previewFpsValue || previewFpsValue is not (30 or 60))
                {
                    SetActionStatus("请选择有效的预览宽度和帧率。");
                    previewWidth.Focus();
                    return;
                }
                updated = new UserPreferences { RecordingFolder = path, FrameRate = fps, PreviewResolution = width, PreviewSizingVersion = 1, PreviewFrameRate = previewFpsValue, OriginalResolution = resolution.SelectedIndex == 1, RecordSystemSound = system.IsChecked == true, RecordMicrophone = microphone.IsChecked == true, RecordCamera = camera.IsChecked == true, MicrophoneId = (MicDevices.SelectedItem as AudioDeviceInfo)?.Id, CameraId = (CameraDevices.SelectedItem as CameraInfo)?.Id, DarkTheme = dark.IsChecked == true, ExcludeEditorDuringRecording = excludeEditor.IsChecked == true, FirstRunHelpShown = current.FirstRunHelpShown };
                try { SaveUserPreferences(updated); }
                catch (Exception ex)
                {
                    App.Log(ex);
                    SetActionStatus("设置未能保存，请检查应用数据权限。");
                    save.Focus();
                    return;
                }
            }
            catch (Exception ex)
            {
                App.Log(ex);
                folderError.Text = "无法使用此目录，请检查路径和磁盘状态。";
                folder.Focus();
                return;
            }
            try { ApplyUserPreferences(updated); }
            catch (Exception ex)
            {
                App.Log(ex);
                SetActionStatus("设置已保存，部分内容未能立即应用。");
                save.Focus();
                return;
            }
            dialog.DialogResult = true;
        };
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

    /// <summary>最近项目显示名：优先工程内的 DisplayName，其次父目录名，最后文件名。</summary>
    public static string DisplayNameFor(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("displayName", out var displayName)
                    && displayName.ValueKind == JsonValueKind.String
                    && displayName.GetString() is { Length: > 0 } name) return name;
            }
        }
        catch { }
        var directory = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrWhiteSpace(directory) ? Path.GetFileNameWithoutExtension(path) : directory;
    }

    public static void Add(string path)
    {
        var diagnosticFlags = new[] { "--visual-states", "--editor-smoke", "--export-panel-smoke", "--recovery-smoke", "--playback-smoke", "--smoke", "--workflow-smoke" };
        if (Environment.GetCommandLineArgs().Any(arg => diagnosticFlags.Contains(arg, StringComparer.OrdinalIgnoreCase))) return;
        var full = Path.GetFullPath(path); var list = Read().Where(x => !string.Equals(x, full, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, full); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(12), new JsonSerializerOptions { WriteIndented = true }));
    }
}
