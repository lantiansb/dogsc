using System.IO;
using System.Text.Json;

namespace FrameTrail.App;

public partial class MainWindow
{
    /// <summary>
    /// B-18 / C-01 / E09: every committed edit used to throw away the playback audio proxy, so the first play after any tweak waited for
    /// a fresh audio export. Mac keeps the audio engine across visual edits; here the proxy file (and the loaded audio player) survive
    /// every edit that leaves the audio mix unchanged. Only the video session, which is bound to the project instance, is rebuilt.
    /// </summary>
    private void InvalidateProxyAfterEdit(FrameTrailProject before, FrameTrailProject after)
    {
        if (proxyPath is not null && ReferenceEquals(playbackProxyProject, before) && File.Exists(proxyPath) && AudioMixKey(before) == AudioMixKey(after))
        {
            ReleasePlaybackSessionKeepingProxy();
            playbackProxyProject = after;
            return;
        }
        InvalidateProxy();
    }

    /// <summary>
    /// Everything ExportEngine's audio path reads (MediaTools.AudioFilter / AudioTrackFilter and the camera audio track): timeline
    /// segments (range, order, rate, per-segment overrides), main volume/mute, independent audio tracks and the camera's audio fields.
    /// </summary>
    internal static string AudioMixKey(FrameTrailProject p) => JsonSerializer.Serialize(new
    {
        p.MediaPath,
        p.Timeline,
        p.Settings.MainAudioVolume,
        p.Settings.MainAudioMuted,
        p.AudioTracks,
        Camera = p.Camera is null ? null : new
        {
            p.Camera.MediaPath, p.Camera.Muted, p.Camera.Volume, p.Camera.SourceStart, p.Camera.Duration, p.Camera.TimelineStart, p.Camera.SyncSegments
        }
    });

    /// <summary>Like DisposePlaybackResourcesAsync, but leaves the exported audio proxy files and the loaded audio player alone.</summary>
    private void ReleasePlaybackSessionKeepingProxy()
    {
        StopPlayback();
        var lifetime = playbackResourcesLifetime;
        playbackResourcesLifetime = new();
        var canceled = lifetime.CancelAsync();
        var previous = playbackWork;
        playbackWork = Task.Run(async () =>
        {
            await canceled.ConfigureAwait(false);
            await previous.ConfigureAwait(false);
            await ReleasePlaybackSessionAsync().ConfigureAwait(false);
            lifetime.Dispose();
        });
    }
}
