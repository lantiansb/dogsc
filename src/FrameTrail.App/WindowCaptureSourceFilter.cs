using FrameTrail.Capture;

namespace FrameTrail.App;

internal static class WindowCaptureSourceFilter
{
    internal static IReadOnlyList<WindowInfo> Filter(
        IEnumerable<WindowInfo> windows,
        int currentProcessId,
        IntPtr mainWindowHandle,
        bool excludeEditorDuringRecording)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Where(window =>
                (window.ProcessId != currentProcessId ||
                (window.Handle == mainWindowHandle && !excludeEditorDuringRecording)) &&
                !IsUnavailable(window.Handle))
            .ToArray();
    }

    /// <summary>Minimised and cloaked (other virtual desktop / suspended app) windows cannot be recorded (Mac window picker).</summary>
    private static bool IsUnavailable(IntPtr handle) =>
        handle != IntPtr.Zero && (PickerNative.IsIconic(handle) || PickerNative.IsCloaked(handle));
}
