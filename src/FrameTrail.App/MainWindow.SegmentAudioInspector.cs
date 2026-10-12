using System.Windows;

namespace FrameTrail.App;

public partial class MainWindow
{
    private Guid? selectedScreenSegmentId;

    /// <summary>
    /// Per-primary-segment audio (Mac audioInspector with a selected segment). Rows for a source the
    /// project does not have are hidden; volumes are 0...100%.
    /// </summary>
    private void BuildSegmentAudioInspector(Guid segmentId)
    {
        var project = this.project!;
        var segment = project.Timeline.Segments.FirstOrDefault(x => x.Id == segmentId);
        if (segment is null) return;

        var overrides = segment.AudioOverrides;
        var systemTrack = project.AudioTracks.FirstOrDefault(x => x.Source == AudioTrackSource.System);
        var microphone = project.AudioTracks.FirstOrDefault(x => x.Source == AudioTrackSource.Microphone);
        var systemAudio = MainSourceHasAudio();

        // C-06 (Mac selectedAudioContext): which segment this is, whether it inherits, jump to it, and clear the override.
        var segmentIndex = project.Timeline.Segments.FindIndex(x => x.Id == segmentId);
        var outputStart = project.Timeline.Segments.Take(segmentIndex).Sum(x => x.OutputDuration);
        var outputEnd = outputStart + segment.OutputDuration;
        var inherits = overrides is null || overrides.IsEmpty;
        Section($"第 {segmentIndex + 1} 段 · {FormatTime(outputStart)} – {FormatTime(outputEnd)}");
        Note(inherits ? "继承全片声音设置" : "已覆盖此段的声音设置");
        ActionButton("定位到此段", () => SeekToOutputTime(outputStart));
        if (!inherits)
            ActionButton("恢复继承", () => InspectorEdit(p =>
            {
                var target = p.Timeline.Segments.FirstOrDefault(x => x.Id == segmentId);
                if (target is not null) target.AudioOverrides = null;
            }, "恢复片段声音继承"));
        if (Playhead.Value < outputStart - 1e-6 || Playhead.Value >= outputEnd - 1e-6)
            Note("播放头在其他位置，当前调整仍只作用于选中片段。");

        Section("所选片段音量");
        Note("未单独设置的项目继承全片音量；覆盖只作用于当前屏幕片段。隐藏音轨不会改变混音。");
        if (systemAudio != false)
        {
            Toggle("静音系统声音", overrides?.SystemMuted ?? (project.Settings.MainAudioMuted || systemTrack?.Muted == true),
                value => SetSegmentAudioOverride(segmentId, x => x.SystemMuted = value, "切换当前片段系统声音"));
            Number("系统声音", Math.Clamp(overrides?.SystemVolume ?? project.Settings.MainAudioVolume, 0, 1), 0, 1,
                value => SetSegmentAudioOverride(segmentId, x => x.SystemVolume = value, null), NumFormat.Percent, log: false, undo: "调整当前片段系统声音");
        }
        if (microphone is not null)
        {
            Toggle("静音麦克风", overrides?.MicrophoneMuted ?? microphone.Muted,
                value => SetSegmentAudioOverride(segmentId, x => x.MicrophoneMuted = value, "切换当前片段麦克风"));
            Number("麦克风", Math.Clamp(overrides?.MicrophoneVolume ?? microphone.Volume, 0, 1), 0, 1,
                value => SetSegmentAudioOverride(segmentId, x => x.MicrophoneVolume = value, null), NumFormat.Percent, log: false, undo: "调整当前片段麦克风");
        }
        else if (systemAudio == false)
        {
            InspectorEmptyState("没有音频轨", "这个项目中没有可编辑的系统声音或麦克风素材。", "trackAudio");
        }
        else
        {
            Note("此项目没有标记为麦克风来源的音轨；导入的普通音轨不自动归类为麦克风。");
        }
    }

    private void SetSegmentAudioOverride(Guid segmentId, Action<PrimarySegmentAudioOverrides> update, string? label)
    {
        InspectorEdit(p =>
        {
            var segment = p.Timeline.Segments.FirstOrDefault(x => x.Id == segmentId);
            if (segment is null) return;
            var overrides = segment.AudioOverrides ??= new PrimarySegmentAudioOverrides();
            update(overrides);
            if (overrides.IsEmpty) segment.AudioOverrides = null;
        }, label);
    }
}
