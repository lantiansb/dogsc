using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FrameTrail.App;

/// <summary>
/// Guards around uncommitted work: running gestures (canvas drag, inspector slider / pad / colour drag, timeline drag or scrub)
/// must never commit over an undo or another edit, editing shortcuts are ignored while one runs, typed inspector text is committed
/// before saving or switching, and selection / inspector tab are made consistent again after an undo or redo.
/// Mac reference: EditorStore.apply cancels the interaction in flight (EditorInteractionState); shortcut monitor ignores edit keys mid drag.
/// </summary>
public partial class MainWindow
{
    // ---- gestures -----------------------------------------------------------------------------------------------------

    /// <summary>Inspector gestures that are mid drag / typing (registered by InspectorGesture.Preview, removed when it ends).</summary>
    private readonly List<InspectorGesture> activeInspectorGestures = [];

    /// <summary>
    /// Ends every running edit gesture without committing it: the canvas drag, inspector drags (their release then commits nothing),
    /// a timeline drag / scrub. (The live preview itself is dropped by the callers, who also decide whether to re-render.) Called before an undo / redo, an edit, or playback starts so a gesture that is
    /// still held can never re-apply its absolute result on top of the new state.
    /// </summary>
    private void AbortActiveGestures()
    {
        var inspectorAborted = activeInspectorGestures.Count > 0;
        if (gesture != CanvasGesture.None) CancelCanvasGesture();
        if (inspectorAborted)
        {
            foreach (var running in activeInspectorGestures.ToArray()) running.Abort();
            DiscardPendingLivePreview();
        }
        if (TimelineView.GestureInProgress) TimelineView.CancelGesture();
        if (inspectorAborted) FlushDeferredInspectorRebuild();
    }

    /// <summary>
    /// Keys that edit the project, move the playhead or start playback (the keys HandleTimelineShortcut acts on). While a gesture or
    /// crop mode owns the editor these are swallowed so nothing changes underneath it.
    /// </summary>
    private static bool IsEditShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Alt) && !modifiers.HasFlag(ModifierKeys.Control)) return key is Key.X;
        if (modifiers.HasFlag(ModifierKeys.Control)) return key is Key.Z or Key.Y or Key.C or Key.V or Key.X or Key.E or Key.O or Key.OemComma or Key.I;
        return key is Key.D or Key.Delete or Key.S or Key.Q or Key.W or Key.Space or Key.I or Key.O or Key.Left or Key.Right;
    }

    // ---- uncommitted typed input ----------------------------------------------------------------------------------------

    /// <summary>
    /// Inspector text boxes commit on focus loss; move the focus to the editor first so a save, close or project switch includes the
    /// text that is still being typed.
    /// </summary>
    private void CommitPendingInput()
    {
        if (Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false } or ComboBox { IsEditable: true }) FocusEditor();
    }

    // ---- focus after a mouse click on a button --------------------------------------------------------------------------

    private bool pointerClickInFlight;

    /// <summary>
    /// A button clicked with the mouse hands the keyboard back to the editor so Space keeps playing / pausing (Mac toolbar buttons do not
    /// keep key focus). A button reached with Tab keeps focus and owns Space / Enter.
    /// </summary>
    private void InitializeButtonFocusReturn()
    {
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            pointerClickInFlight = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => pointerClickInFlight = false));
        }), true);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClickReturnFocus), true);
    }

    private void OnButtonClickReturnFocus(object sender, RoutedEventArgs e)
    {
        if (!pointerClickInFlight || e.OriginalSource is not ButtonBase button || button is RepeatButton) return;
        for (DependencyObject? node = button; node is not null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ComboBox or ScrollBar or Slider or TabControl) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // Only when the button still holds the focus: a menu, dialog or text box opened by the click keeps it.
            if (ReferenceEquals(Keyboard.FocusedElement, button) && !cropMode) FocusEditor();
        }));
    }

    // ---- selection after undo / redo --------------------------------------------------------------------------------------

    /// <summary>
    /// After an undo / redo makes selectedEffect, TimelineView.Selection, selectedScreenSegmentId and the inspector tab agree again
    /// and drops selections of objects that no longer exist. (Mac EditorStore.reconcileSelection)
    /// </summary>
    private void NormalizeSelectionAfterHistory()
    {
        if (project is null) return;
        if (selectedEffect is { } effect && !SelectionExists(project, effect)) selectedEffect = null;
        if (selectedScreenSegmentId is { } segmentId && project.Timeline.Segments.All(x => x.Id != segmentId)) selectedScreenSegmentId = null;
        if (selectedEffect is not null) selectedScreenSegmentId = null;

        if (selectedEffect is { } selected)
        {
            var tab = selected.Kind switch
            {
                "camera" or "cameraMotion" => "camera",
                "audio" => "audio",
                "overlay" => "overlay",
                "blur" => "blur",
                _ => "zoom"
            };
            if (inspectorTab != tab)
            {
                inspectorTab = tab;
                if (tab == "zoom") motionTab = selected.Kind == "motion" ? "motion" : "zoom";
            }
            else if (tab == "zoom" && selected.Kind == "motion") motionTab = "motion";
            TimelineView.Selection = selected;
        }
        else if (selectedScreenSegmentId is { } screen)
        {
            inspectorTab = "audio";
            TimelineView.Selection = new("screen", screen);
        }
        else
        {
            // Nothing selected: tabs that only make sense for a selected object fall back to the frame tab.
            if (inspectorTab is "overlay" or "blur" || inspectorTab == "camera" && project.Camera is null) inspectorTab = "frame";
            TimelineView.Selection = null;
        }
        var stale = TimelineView.MultiSelection.Where(x => !SelectionExists(project, x)).ToList();
        foreach (var item in stale) TimelineView.MultiSelection.Remove(item);
        if (TimelineView.Selection is { } visible && TimelineView.MultiSelection.Count == 0) TimelineView.MultiSelection.Add(visible);
    }

    // ---- committed frame after a failed / rejected edit -------------------------------------------------------------------

    /// <summary>The project whose last render failed; clicks on the canvas stop waiting for a fresh scene for it.</summary>
    private FrameTrailProject? previewRenderFailedProject;

    /// <summary>Renders the committed project at the playhead and remembers a failure so the canvas does not wait for a scene that never comes.</summary>
    private async Task RenderCommittedPreview()
    {
        var rendered = project;
        try { await RenderPreview(Playhead.Value); }
        catch { previewRenderFailedProject = rendered; throw; }
    }
}
