using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>
/// Shared plumbing for inspector controls: live (uncommitted) preview while a control is being
/// dragged or typed into, one undo step on release, focus/scroll preservation across rebuilds
/// and the scope header. Mac: EditorStore.beginContinuousInteraction / updateInteraction /
/// commitInteraction (EditorInspectorTransactions.swift) and EditorInspectorView scroll contexts.
/// </summary>
public partial class MainWindow
{
    private enum InspectorEditMode { Commit, Preview }

    private InspectorEditMode inspectorMode = InspectorEditMode.Commit;
    private string? inspectorCommitLabel;

    /// <summary>Number of controls currently inside a drag / typing transaction.</summary>
    private int inspectorTransactions;
    private bool inspectorRebuildDeferred;

    /// <summary>
    /// Inspector replacement for ApplyEdit. While a control is mid-gesture the edit only drives the
    /// live preview; on release the very same setter is committed once with a Chinese action name.
    /// </summary>
    private void InspectorEdit(Action<FrameTrailProject> edit, string? label = null)
    {
        if (inspectorMode == InspectorEditMode.Preview)
        {
            PreviewLiveEdit(edit);
            return;
        }
        if (buildingInspector) return;
        CommitLiveEdit(edit, label ?? inspectorCommitLabel);
    }

    /// <summary>Runs a control setter so that any InspectorEdit it performs previews or commits.</summary>
    private void RunInspectorSetter(InspectorEditMode mode, string? label, Action setter)
    {
        var previousMode = inspectorMode;
        var previousLabel = inspectorCommitLabel;
        inspectorMode = mode;
        inspectorCommitLabel = label;
        try { setter(); }
        finally
        {
            inspectorMode = previousMode;
            inspectorCommitLabel = previousLabel;
        }
    }

    /// <summary>One-shot (non-gesture) commit used by buttons, toggles and choices.</summary>
    private void CommitInspectorAction(string? label, Action action) =>
        RunInspectorSetter(InspectorEditMode.Commit, label, action);

    // ---- Throttled live preview ---------------------------------------------------------------
    private const double LivePreviewIntervalMs = 28;
    private DispatcherTimer? livePreviewTimer;
    private Action? livePreviewPending;
    private long livePreviewLastRun;

    private void PostLivePreview(Action action)
    {
        var elapsed = Stopwatch.GetElapsedTime(livePreviewLastRun).TotalMilliseconds;
        if (livePreviewTimer is not { IsEnabled: true } && (livePreviewLastRun == 0 || elapsed >= LivePreviewIntervalMs))
        {
            livePreviewLastRun = Stopwatch.GetTimestamp();
            action();
            return;
        }
        livePreviewPending = action;
        if (livePreviewTimer is null)
        {
            livePreviewTimer = new DispatcherTimer(DispatcherPriority.Input);
            livePreviewTimer.Tick += (_, _) =>
            {
                livePreviewTimer.Stop();
                var pending = livePreviewPending;
                livePreviewPending = null;
                livePreviewLastRun = Stopwatch.GetTimestamp();
                pending?.Invoke();
            };
        }
        if (!livePreviewTimer.IsEnabled)
        {
            livePreviewTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(LivePreviewIntervalMs - elapsed, 1, LivePreviewIntervalMs));
            livePreviewTimer.Start();
        }
    }

    private void DiscardPendingLivePreview()
    {
        livePreviewTimer?.Stop();
        livePreviewPending = null;
    }

    private void FlushDeferredInspectorRebuild()
    {
        if (inspectorRebuildDeferred && inspectorTransactions == 0) BuildInspector();
    }

    /// <summary>
    /// One drag / typing / keyboard-step interaction of a single control. Preview calls update the
    /// live preview; Commit applies the final value once; Cancel restores the committed frame.
    /// </summary>
    private sealed class InspectorGesture(MainWindow owner, string? label)
    {
        public bool Active { get; private set; }
        // Set when an undo / edit / playback start ended this gesture while the control was still held: the rest of the drag and the
        // release must not touch the project (the release would otherwise re-apply the absolute value over the new state).
        private bool aborted;

        public void Preview(Action setter)
        {
            if (aborted) return;
            if (!Active)
            {
                Active = true;
                owner.inspectorTransactions++;
                owner.activeInspectorGestures.Add(this);
            }
            owner.PostLivePreview(() => owner.RunInspectorSetter(InspectorEditMode.Preview, label, setter));
        }

        public void Commit(Action setter)
        {
            End();
            owner.DiscardPendingLivePreview();
            if (aborted)
            {
                aborted = false;
                owner.FlushDeferredInspectorRebuild();
                return;
            }
            owner.RunInspectorSetter(InspectorEditMode.Commit, label, setter);
            owner.FlushDeferredInspectorRebuild();
        }

        public void Cancel()
        {
            if (aborted) { aborted = false; return; }
            if (!Active) return;
            End();
            owner.DiscardPendingLivePreview();
            owner.CancelLiveEdit();
            owner.FlushDeferredInspectorRebuild();
        }

        /// <summary>Ends the running gesture without committing or re-rendering; the control's release is ignored.</summary>
        public void Abort()
        {
            if (!Active) return;
            End();
            aborted = true;
        }

        private void End()
        {
            if (!Active) return;
            Active = false;
            owner.inspectorTransactions = Math.Max(0, owner.inspectorTransactions - 1);
            owner.activeInspectorGestures.Remove(this);
        }
    }

    // ---- Focus -------------------------------------------------------------------------------
    private static readonly DependencyProperty InspectorFocusKeyProperty =
        DependencyProperty.RegisterAttached("InspectorFocusKey", typeof(string), typeof(MainWindow));

    private readonly Dictionary<string, FrameworkElement> inspectorFocusTargets = [];
    private readonly Dictionary<string, int> inspectorKeyCounts = [];

    /// <summary>Marks a control so keyboard focus can be restored after the panel is rebuilt.</summary>
    private void RegisterInspectorFocus(FrameworkElement element, string label)
    {
        inspectorKeyCounts.TryGetValue(label, out var index);
        inspectorKeyCounts[label] = index + 1;
        var key = inspectorTab + "/" + label + "#" + index;
        element.SetValue(InspectorFocusKeyProperty, key);
        inspectorFocusTargets[key] = element;
    }

    private string? CapturedInspectorFocusKey()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused) return null;
        for (DependencyObject? node = focused; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node.GetValue(InspectorFocusKeyProperty) is string key) return key;
            if (ReferenceEquals(node, InspectorHost)) break;
        }
        return null;
    }

    private bool releaseFocusAfterCommit;

    /// <summary>
    /// Hands the keyboard back to the editor after a pointer/Enter edit so Space, Ctrl+Z, Delete
    /// and the timeline shortcuts keep working. Keyboard-driven adjustments keep their focus.
    /// </summary>
    private void FocusEditor()
    {
        Keyboard.ClearFocus();
        TimelineView.Focus();
    }

    /// <summary>Commits with focus released afterwards, even when the commit rebuilds the panel.</summary>
    private void CommitAndReleaseFocus(Action commit)
    {
        releaseFocusAfterCommit = true;
        try { commit(); }
        finally { releaseFocusAfterCommit = false; }
        FocusEditor();
    }

    private void RestoreInspectorFocus(string? key)
    {
        if (key == null || !inspectorFocusTargets.TryGetValue(key, out var element)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!element.IsVisible || !element.IsEnabled) return;
            element.Focus();
            if (element is TextBox box) box.CaretIndex = box.Text.Length;
        });
    }

    // ---- Scroll memory (Mac EditorInspectorScrollContext: panel + selected object) --------------
    private readonly Dictionary<string, double> inspectorScrollMemory = [];
    private string? inspectorScrollKey;

    private string CurrentInspectorScrollKey()
    {
        var sub = inspectorTab switch { "frame" => frameTab, "zoom" => motionTab, _ => "" };
        var id = inspectorTab == "audio" && selectedScreenSegmentId is { } segment ? segment : selectedEffect?.Id;
        return inspectorTab + ":" + sub + ":" + id;
    }

    private void RememberInspectorScroll()
    {
        if (inspectorScrollKey == null || InspectorScroll == null) return;
        inspectorScrollMemory[inspectorScrollKey] = InspectorScroll.VerticalOffset;
        if (inspectorScrollMemory.Count > 80)
        {
            var keep = inspectorScrollMemory.Keys.Skip(40).ToArray();
            var kept = keep.ToDictionary(x => x, x => inspectorScrollMemory[x]);
            inspectorScrollMemory.Clear();
            foreach (var pair in kept) inspectorScrollMemory[pair.Key] = pair.Value;
        }
    }

    private void RestoreInspectorScroll()
    {
        inspectorScrollKey = CurrentInspectorScrollKey();
        var offset = inspectorScrollMemory.GetValueOrDefault(inspectorScrollKey, 0);
        InspectorScroll.UpdateLayout();
        InspectorScroll.ScrollToVerticalOffset(offset);
        var expectedKey = inspectorScrollKey;
        if (offset > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (inspectorScrollKey == expectedKey) InspectorScroll.ScrollToVerticalOffset(offset);
            });
    }

    // ---- Scope header (Mac EditorInspectorScopeControls) --------------------------------------
    private string? InspectorScopeDescription()
    {
        if (project == null) return null;
        var p = project;
        if (inspectorTab == "audio" && selectedScreenSegmentId is { } segmentId)
        {
            var index = p.Timeline.Segments.FindIndex(x => x.Id == segmentId);
            return index >= 0 ? $"第 {index + 1} 段" : null;
        }
        if (selectedEffect is not { } s) return null;
        return s.Kind switch
        {
            "zoom" when p.ZoomClips.Any(x => x.Id == s.Id) => "缩放片段",
            "motion" when p.ScreenMotions.Any(x => x.Id == s.Id) => "屏幕 3D 片段",
            "cameraMotion" when p.CameraMotions.Any(x => x.Id == s.Id) => "摄像头动画",
            "overlay" when p.Overlays.Any(x => x.Id == s.Id) => "贴图",
            "blur" when p.BlurClips.Any(x => x.Id == s.Id) => "柔化区域",
            "camera" when p.Camera != null => "摄像头素材",
            "audio" => "音轨",
            _ => null
        };
    }

    private void UpdateInspectorScope()
    {
        var scope = InspectorScopeDescription();
        InspectorScopeText.Text = scope == null ? "全片" : "所选：" + scope;
        InspectorScopeClear.Visibility = scope == null ? Visibility.Collapsed : Visibility.Visible;
        InspectorScopeChip.Background = scope == null ? Brush("ControlSurface") : Brush("Selection");
        System.Windows.Automation.AutomationProperties.SetName(InspectorScopeChip, scope == null ? "检查器作用范围：全片" : "检查器作用范围：所选" + scope);
    }

    private void InspectorScopeClearClick(object sender, RoutedEventArgs e)
    {
        selectedEffect = null;
        selectedScreenSegmentId = null;
        TimelineView.Selection = null;
        TimelineView.MultiSelection.Clear();
        TimelineView.InvalidateVisual();
        BuildInspector();
        RefreshPreviewSelection();
        FocusEditor();
    }

    private void UpdateInspectorRail()
    {
        // Overlay / blur / crop panels belong to the "画面" rail entry (Mac EditorInspectorRouting).
        var railTab = inspectorTab is "overlay" or "blur" or "crop" ? "frame" : inspectorTab;
        foreach (var button in ToolRail.Children.OfType<Button>())
        {
            var tag = (string)button.Tag;
            button.Background = tag == railTab ? Brush("Selection") : Brushes.Transparent;
            button.IsEnabled = tag != "camera" || project?.Camera != null || project == null;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, tag == railTab ? "当前面板" : "");
        }
    }
}
