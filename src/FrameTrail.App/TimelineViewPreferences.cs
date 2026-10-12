using System.IO;
using System.Text.Json;

namespace FrameTrail.App;

/// <summary>
/// Per-project timeline view state (zoom factor and hidden tracks) kept in a small json file under
/// LocalAppData\FrameTrail. Mac reference: AppPreferences.rememberTimelineZoom(_:for:).
/// Every operation swallows I/O problems: the view state is a convenience, never a reason to fail.
/// </summary>
internal static class TimelineViewPreferences
{
    internal sealed class Entry
    {
        public double Zoom { get; set; } = 1;
        public List<string> HiddenTracks { get; set; } = [];
    }

    private const int MaximumEntries = 200;
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "timeline-view.json");

    public static Entry? Load(Guid projectId)
    {
        try
        {
            lock (Gate)
            {
                var all = ReadAll();
                return all.TryGetValue(projectId.ToString("N"), out var entry) ? entry : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(Guid projectId, double zoom, IEnumerable<string> hiddenTracks)
    {
        try
        {
            lock (Gate)
            {
                var all = ReadAll();
                all[projectId.ToString("N")] = new Entry
                {
                    Zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, EditorTimelineControl.MaxZoom) : 1,
                    HiddenTracks = hiddenTracks.ToList()
                };
                // Bound the file: drop arbitrary oldest-by-insertion entries beyond the cap.
                while (all.Count > MaximumEntries) all.Remove(all.Keys.First());
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all));
                File.Move(temporary, FilePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<string, Entry> ReadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}
