using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.App;

public sealed partial class ExportWindow
{
    // Diagnostic entry point: exercises the actual controls and media engine in an isolated output folder.
    internal async Task<IReadOnlyList<string>> VerifyPanelAsync(string outputFolder)
    {
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        async Task WaitUntil(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("导出面板验证超时。");
                await Task.Delay(15);
            }
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Snapshot(string state) => MainWindow.CaptureWindowVisual(this,
            Path.Combine(outputFolder, $"panel-{(audioOnly ? "audio" : "video")}-{state}.png"));

        outputDirectory = outputFolder;
        fileName.Text = audioOnly ? "panel-audio" : "panel-video";
        RefreshPreview();
        UpdateLayout();
        var cancelBoundsReady = GetButtonBounds(cancelButton);
        var primaryBoundsReady = GetButtonBounds(exportButton);
        Check(openFolderButton.Visibility == Visibility.Collapsed && openFileButton.Visibility == Visibility.Collapsed && exportButton.IsVisible,
            "导出就绪时完成态按钮折叠，只显示开始导出");

        Snapshot("ready");
        if (audioOnly)
        {
            Check(extensionBadge.Text == ".m4a" && previewFormat.Text == "M4A 音频" && resolutionChoices.All(b => !b.IsEnabled) && frameRateChoices.All(b => !b.IsEnabled),
                "音频专用入口直接显示M4A模式并禁用视频分辨率和帧率");
            Click(exportButton);
            await WaitUntil(() => !running);
            Check(completedPath is not null && File.Exists(completedPath), "音频面板实际生成文件");
            var info = await MediaTools.ProbeAsync(completedPath!);
            Check(info.HasAudio && info.Width == 0 && Math.Abs(info.Duration - project.Timeline.Duration) < .12,
                "M4A只有音频且时长与剪辑一致");
            Check(openFileButton.IsVisible && openFolderButton.IsVisible && exportButton.IsVisible,
                "成功后展示打开文件、在资源管理器中显示与再次导出");
            CheckButtonBounds(primaryBoundsReady, exportButton, "音频完成再次导出");
            Snapshot("completed");
            return checks;
        }

        var blockedDirectory = Path.Combine(outputFolder, "blocked-output");
        await File.WriteAllTextAsync(blockedDirectory, "preserve-existing-file");
        outputDirectory = blockedDirectory;
        RefreshPreview();
        Click(exportButton);
        await WaitUntil(() => !running);
        Check(completedPath is null && stageText.Text == "导出失败，可修改设置后重试" && exportButton.IsEnabled,
            "输出目录不可用时显示失败并恢复重试操作");
        Check(errorDetailsButton.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(exportErrorDetails),
            "导出失败时显示错误详情入口并保留异常文本");
        Check(await File.ReadAllTextAsync(blockedDirectory) == "preserve-existing-file", "失败不会改写已有目标文件");
        Snapshot("failed");
        VerifyErrorDetailsDialog();
        outputDirectory = outputFolder;
        RefreshPreview();
        Click(resolutionChoices[0]);
        var targetPath = Path.Combine(outputFolder, "panel-video.mp4");
        var originalTarget = System.Text.Encoding.UTF8.GetBytes("既有目标内容：取消不得覆盖");
        await File.WriteAllBytesAsync(targetPath, originalTarget);
        // 同名文件不再弹覆盖确认：导出自动改用 -2。
        var secondPath = Path.Combine(outputFolder, "panel-video-2.mp4");
        Click(exportButton);
        var cancelProgress = BeginProgressSampling();
        await WaitUntil(() => !running || progressBar.Value > .05 && cancelProgress.Count >= 3);
        cancelProgress.Add((progressBar.Value, stageText.Text));
        Check(running && progressBar.Value > .05 && stageText.Text.Contains("编码"), "同名文件自动改名后实际编码进度到达后可以取消");
        CheckCancelButtonBounds(cancelBoundsReady, "视频运行中");
        CheckButtonBounds(primaryBoundsReady, exportButton, "视频运行中主操作");
        Check(openFolderButton.Visibility == Visibility.Collapsed && errorDetailsButton.Visibility == Visibility.Hidden,
            "运行中保留文件夹按钮位置并清除旧错误详情入口");
        Check(!videoModeButton.IsEnabled && !audioModeButton.IsEnabled && !browseButton.IsEnabled && !fileName.IsEnabled,
            "导出进行中锁定内容类型和输出位置，摘要与当前任务一致");
        Check(cancelProgress.Count >= 1 && cancelProgress.Max(x => x.Fraction) > 0 && IsMonotonic(cancelProgress),
            $"取消前进度单调且大于零（{DescribeProgress(cancelProgress)}）");
        Snapshot("running");
        Check(cancelButton.Content as string == "取消导出", "导出中取消按钮文案为取消导出");
        Click(cancelButton);
        await WaitUntil(() => !running);
        Check(completedPath is null && stageText.Text == "已取消导出" && exportButton.IsEnabled && cancelButton.IsEnabled,
            "取消完成后恢复导出按钮且不标记成功");
        Check(File.Exists(targetPath) && (await File.ReadAllBytesAsync(targetPath)).AsSpan().SequenceEqual(originalTarget),
            "取消覆盖任务保留原有目标文件的完整字节");
        Check(!Directory.EnumerateFiles(outputFolder, ".frametrail-*", SearchOption.TopDirectoryOnly).Any(),
            "取消后没有导出临时文件残留");
        Snapshot("cancelled");
        Click(exportButton);
        var retryProgress = BeginProgressSampling();
        await WaitUntil(() => !running);
        retryProgress.Add((progressBar.Value, stageText.Text));
        Check(completedPath is not null && File.Exists(completedPath), "取消后在同一面板重新导出成功");
        Check(completedPath == secondPath && (await File.ReadAllBytesAsync(targetPath)).AsSpan().SequenceEqual(originalTarget),
            "同面板重试写入 -2 新文件且不覆盖既有目标文件");
        Check(retryProgress.Select(x => x.Fraction).Distinct().Count() >= 2
            && Math.Abs(retryProgress[^1].Fraction - 1) < 1e-9
            && IsMonotonic(retryProgress),
            $"重试进度单调、包含变化并到达100%（{DescribeProgress(retryProgress)}）");
        var video = await MediaTools.ProbeAsync(completedPath!);
        Check(video.Width > 0 && Math.Abs(video.Duration - project.Timeline.Duration) < .12,
            "重试成片可解码且时长正确");
        Check(!Directory.EnumerateFiles(outputFolder, ".frametrail-*", SearchOption.TopDirectoryOnly).Any(),
            "重试成功后没有导出临时文件残留");
        var decoded = await DecodeAllAsync(completedPath!);
        Check(decoded.ExitCode == 0,
            $"重试成片完整解码成功（FFmpeg退出码 {decoded.ExitCode}；{MediaTools.Tail(decoded.Error)}）");
        Check(openFolderButton.Visibility == Visibility.Visible && openFileButton.MinWidth == 145 && exportButton.MinWidth == 145 && exportButton.IsVisible,
            "完成后文件夹按钮显示且打开文件与再次导出按钮保持相同主操作宽度");
        CheckButtonBounds(primaryBoundsReady, exportButton, "视频完成再次导出");
        Click(resolutionChoices[1]);
        Check(completedPath is null && !openFileButton.IsVisible && !openFolderButton.IsVisible,
            "完成后修改导出参数会重置完成态");
        Click(resolutionChoices[0]);
        Snapshot("completed");
        // A close request during export must wait for actual engine cleanup.
        fileName.Text = "panel-close-during-export";
        ConfirmCancelOnClose = () => true;
        await StartCloseRegression();
        Check(!IsVisible && !running && completedPath is null && !File.Exists(Path.Combine(outputFolder, "panel-close-during-export.mp4"))
            && !Directory.EnumerateFiles(outputFolder, ".frametrail-*").Any(), "关闭正在导出的窗口会等待引擎取消及临时文件清理");
        return checks;

        async Task StartCloseRegression()
        {
            var task = StartExport();
            await WaitUntil(() => !running || progressBar.Value > .05);
            Check(running && IsVisible, "关闭回归使用实际运行中的视频导出");
            Close();
            Check(IsVisible && closeAfterCancellation, "关闭请求被暂缓，取消状态立即显示");
            await task;
        }

        List<(double Fraction, string Stage)> BeginProgressSampling()
        {
            var values = new List<(double Fraction, string Stage)>();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                if (!running) timer.Stop();
                else if (double.IsFinite(progressBar.Value) && progressBar.Value > 0)
                    values.Add((progressBar.Value, stageText.Text));
            };
            timer.Start();
            return values;
        }

        Rect GetButtonBounds(Button button)
        {
            var origin = button.TranslatePoint(new Point(0, 0), this);
            return new Rect(origin, new Size(button.ActualWidth, button.ActualHeight));
        }

        void CheckCancelButtonBounds(Rect expected, string state)
        {
            CheckButtonBounds(expected, cancelButton, state + "取消");
        }

        void CheckButtonBounds(Rect expected, Button button, string state)
        {
            UpdateLayout();
            var actual = GetButtonBounds(button);
            Check(Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1
                && Math.Abs(actual.Width - expected.Width) <= 1 && Math.Abs(actual.Height - expected.Height) <= 1,
                $"{state}按钮边界相对就绪状态变化不超过1 DIP（ready={expected}; actual={actual}）");
        }

        void VerifyErrorDetailsDialog()
        {
            Window? dialog = null;
            string? detailsText = null;
            var copyButtonFound = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var started = Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                dialog = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window != this && window.Title == "导出错误详情");
                if (dialog is not null)
                {
                    timer.Stop();
                    detailsText = FindLogicalTextBox(dialog)?.Text;
                    copyButtonFound = FindLogicalButton(dialog, "复制详情") is not null;
                    MainWindow.CaptureWindowVisual(dialog, Path.Combine(outputFolder, "panel-video-error-details.png"));
                    if (FindLogicalButton(dialog, "关闭") is { } close)
                        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else
                        dialog.Close();
                    return;
                }
                if (started.Elapsed > TimeSpan.FromSeconds(5)) timer.Stop();
            };
            timer.Start();
            errorDetailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            timer.Stop();
            Check(dialog is not null && detailsText is not null && detailsText == exportErrorDetails && detailsText.Contains("blocked-output", StringComparison.Ordinal),
                "错误详情弹窗显示完整失败信息并由应用内测试关闭");
            Check(copyButtonFound, "错误详情弹窗提供复制详情操作");
        }

        static Button? FindLogicalButton(DependencyObject root, string label)
        {
            if (root is Button button && string.Equals(Convert.ToString(button.Content), label, StringComparison.Ordinal)) return button;
            foreach (var child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject dependency && FindLogicalButton(dependency, label) is { } found) return found;
            return null;
        }

        static TextBox? FindLogicalTextBox(DependencyObject root)
        {
            if (root is TextBox textBox) return textBox;
            foreach (var child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject dependency && FindLogicalTextBox(dependency) is { } found) return found;
            return null;
        }

        static bool IsMonotonic(IReadOnlyList<(double Fraction, string Stage)> values)
        {
            for (var index = 1; index < values.Count; index++)
                if (values[index].Fraction + 1e-9 < values[index - 1].Fraction) return false;
            return true;
        }

        static string DescribeProgress(IReadOnlyList<(double Fraction, string Stage)> values)
        {
            var minimum = values.Min(x => x.Fraction);
            var maximum = values.Max(x => x.Fraction);
            var stages = string.Join("、", values.Select(x => x.Stage).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            return $"count={values.Count}, min={minimum:0.000}, max={maximum:0.000}, stages=[{stages}]";
        }

        static async Task<(int ExitCode, string Error)> DecodeAllAsync(string path)
        {
            using var process = MediaTools.Start(MediaTools.Ffmpeg,
                ["-v", "error", "-i", path, "-f", "null", "NUL"]);
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                MediaTools.Kill(process);
                await process.WaitForExitAsync();
                throw;
            }
            return (process.ExitCode, await errorTask);
        }
    }
}

public partial class MainWindow
{
    private async Task VerifyExportPanel(string reportPath)
    {
        var checks = new List<string>();
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "export-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        object result;
        try
        {
            if (project is null || projectFile is null) throw new InvalidOperationException("必须打开含音轨的项目。");
            StopPlayback();
            foreach (var audio in new[] { true, false })
            {
                var snapshot = Clone(project);
                if (!audio) ExtendExportProgressFixture(snapshot, targetDurationSeconds: 32);
                var file = projectFile;
                var source = FrameTrailProjectStore.ResolveMediaPath(file, snapshot);
                var window = new ExportWindow(snapshot, PreviewImage.Source, "导出面板回归", (path, options, progress, token) =>
                    Task.Run(() => new ExportEngine().ExportAsync(snapshot, source, path, options with { ProjectFilePath = file }, progress, token)), initialAudioOnly: audio) { Owner = this };
                try { window.Show(); checks.AddRange(await window.VerifyPanelAsync(output)); }
                finally { window.Close(); }
            }
            result = new { Passed = true, Checks = checks, Output = output };
        }
        catch (Exception ex) { App.Log(ex); result = new { Passed = false, Checks = checks, Output = output, Error = ex.ToString() }; }
        await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ExtendExportProgressFixture(FrameTrailProject snapshot, double targetDurationSeconds)
    {
        var template = snapshot.Timeline.Segments.FirstOrDefault()
            ?? throw new InvalidDataException("导出进度夹具没有主时间线片段。");
        var repeatCount = (int)Math.Clamp(Math.Ceiling(targetDurationSeconds / template.OutputDuration), 2d, 16d);
        snapshot.Timeline.Segments = Enumerable.Range(0, repeatCount)
            .Select(_ => template.Copy(Guid.NewGuid()))
            .ToList();
        snapshot.Timeline.Validate();
    }
}
