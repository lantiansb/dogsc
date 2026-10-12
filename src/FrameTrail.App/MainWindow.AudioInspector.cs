using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrameTrail.Media;

namespace FrameTrail.App;

/// <summary>
/// Whole-recording audio panel. Mac: EditorInspectorMediaControls.audioInspector — a source row is
/// only shown when that source really carries audio, volumes run 0...100%.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, bool> mediaHasAudio = [];
    private readonly HashSet<string> mediaAudioProbing = [];

    /// <summary>Whether the media file has an audio stream; null while the first probe is still running.</summary>
    private bool? MediaHasAudioKnown(string path)
    {
        if (mediaHasAudio.TryGetValue(path, out var known)) return known;
        if (mediaAudioProbing.Add(path)) _ = ProbeMediaAudioAsync(path);
        return null;
    }

    private async Task ProbeMediaAudioAsync(string path)
    {
        var hasAudio = true;
        try { hasAudio = (await MediaTools.ProbeAsync(path)).HasAudio; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Unknown media: keep the row visible rather than hiding a source the user may need.
        }
        mediaAudioProbing.Remove(path);
        mediaHasAudio[path] = hasAudio;
        if (inspectorTab == "audio" && !buildingInspector && inspectorTransactions == 0) BuildInspector();
    }

    private bool? MainSourceHasAudio() =>
        project == null || projectFile == null ? null : MediaHasAudioKnown(FrameTrailProjectStore.ResolveMediaPath(projectFile, project));

    private bool? CameraHasAudio(CameraTrack camera) =>
        projectFile == null ? null : MediaHasAudioKnown(FrameTrailProjectStore.ResolveAssetPath(projectFile, camera.MediaPath));

    private void BuildAudioInspector()
    {
        var p = project!;
        var systemAudio = MainSourceHasAudio();
        var cameraAudio = p.Camera is { } cam ? CameraHasAudio(cam) : false;
        var microphoneTracks = p.AudioTracks.Select((track, index) => (track, index)).Where(x => x.track.Source != AudioTrackSource.System).ToArray();
        var anyRow = systemAudio != false || cameraAudio != false || microphoneTracks.Length > 0;

        if (anyRow)
        {
            Section("全片音轨");
            Note("音量和静音作用于整条素材，剪辑、变速和重排后按最终时间线混合。");
            if (systemAudio != false)
                AudioVolumeRow("系统声音", "录屏中的应用与系统声音", "screen", p.Settings.MainAudioMuted, p.Settings.MainAudioVolume,
                    v => InspectorEdit(x => x.Settings.MainAudioMuted = v, "切换系统声音"), v => InspectorEdit(x => x.Settings.MainAudioVolume = v));
            if (p.Camera != null && cameraAudio != false)
                AudioVolumeRow("摄像头声音", "摄像头素材自带的声音", "camera", p.Camera.Muted, p.Camera.Volume,
                    v => InspectorEdit(x => x.Camera!.Muted = v, "切换摄像头声音"), v => InspectorEdit(x => x.Camera!.Volume = v));
            foreach (var (track, index) in microphoneTracks)
            {
                var i = index;
                var title = track.Source == AudioTrackSource.Microphone ? "麦克风" : "独立音轨 " + (i + 1);
                AudioVolumeRow(title, track.Source == AudioTrackSource.Microphone ? "独立录制的人声轨道" : "导入的声音素材", "audio", track.Muted, track.Volume,
                    v => InspectorEdit(x => x.AudioTracks[i].Muted = v, "切换" + title), v => InspectorEdit(x => x.AudioTracks[i].Volume = v));
            }
        }
        else if (systemAudio == null || cameraAudio == null)
        {
            Note("正在检测声音轨…", true);
        }
        else
        {
            EmptyState("没有音频轨", "这个项目中没有可编辑的系统声音或麦克风素材。", "trackAudio");
        }

        for (var i = 0; i < p.AudioTracks.Count; i++)
        {
            var index = i;
            var a = p.AudioTracks[i];
            if (a.Source == AudioTrackSource.System) continue;
            InspectorDisclosure($"音轨 {i + 1} · 同步与素材", () =>
            {
                Note(Path.GetFileName(a.MediaPath));
                Number("时间偏移", a.OffsetSeconds * 1000, -10000, 10000,
                    v => InspectorEdit(x => AudioTimelineOperations.Move(x.AudioTracks[index], v / 1000 - x.AudioTracks[index].OffsetSeconds)),
                    NumFormat.Milliseconds, log: false, undo: "校准音轨时间");
                NudgeRow("音轨", ms => InspectorEdit(x => AudioTimelineOperations.Move(x.AudioTracks[index], ms / 1000), "校准音轨时间"),
                    () => InspectorEdit(x => AudioTimelineOperations.Move(x.AudioTracks[index], -x.AudioTracks[index].OffsetSeconds), "归零音轨时间"),
                    Math.Abs(a.OffsetSeconds) > 1e-4);
                ActionButton("移除音轨", () => InspectorEdit(x => x.AudioTracks.RemoveAt(index), "移除音轨"));
            });
        }
        ActionButton("导入音频…", () => _ = Guard(ImportAudio));
    }

    /// <summary>One audio source: title, mute switch and a 0...100% volume slider (Mac audioVolumeRow).</summary>
    private void AudioVolumeRow(string title, string detail, string icon, bool muted, double volume, Action<bool> mute, Action<double> change)
    {
        var row = new DockPanel { Margin = new(0, 20, 0, 3) };
        row.Children.Add(new EditorIcon { Kind = icon, Width = 17, Height = 17, Margin = new(0, 0, 8, 0), Stroke = Brush("Secondary") });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = Brush("Secondary"), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        InspectorHost.Children.Add(row);
        Toggle("静音", muted, mute);
        // Mac caps volume at 100%; a project that already stores more keeps its value reachable.
        Number("音量", volume, 0, Math.Max(1, volume), change, NumFormat.Percent, log: false, undo: "调整" + title + "音量");
    }
}
