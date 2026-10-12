using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace FrameTrail.App;

/// <summary>A numeric editor commits one undo transaction for drag, keyboard or accessibility edits.</summary>
public sealed class InspectorSlider : Slider
{
    public event EventHandler? AutomationValueCommitted;

    /// <summary>
    /// Maps the slider's travel position to the parameter value when the travel is not linear
    /// (Mac EditorSliderScale.logarithmic). Automation clients always see the parameter value.
    /// </summary>
    public Func<double, double>? ToParameter { get; set; }
    public Func<double, double>? FromParameter { get; set; }
    private double Param(double position) => ToParameter?.Invoke(position) ?? position;
    private double Position(double parameter) => FromParameter?.Invoke(parameter) ?? parameter;
    protected override AutomationPeer OnCreateAutomationPeer() => new InspectorSliderPeer(this);

    private sealed class InspectorSliderPeer(InspectorSlider owner) : SliderAutomationPeer(owner), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        bool IRangeValueProvider.IsReadOnly => false;
        double IRangeValueProvider.LargeChange => owner.Param(owner.Minimum + owner.LargeChange) - owner.Param(owner.Minimum);
        double IRangeValueProvider.SmallChange => owner.Param(owner.Minimum + owner.SmallChange) - owner.Param(owner.Minimum);
        double IRangeValueProvider.Maximum => owner.Param(owner.Maximum);
        double IRangeValueProvider.Minimum => owner.Param(owner.Minimum);
        double IRangeValueProvider.Value => owner.Param(owner.Value);
        void IRangeValueProvider.SetValue(double value)
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < owner.Param(owner.Minimum) - 1e-9 || value > owner.Param(owner.Maximum) + 1e-9)
                throw new ArgumentOutOfRangeException(nameof(value));
            owner.SetCurrentValue(ValueProperty, Math.Clamp(owner.Position(value), owner.Minimum, owner.Maximum));
            // The commit rebuilds the inspector. Finish the provider call before replacing its UI tree.
            owner.Dispatcher.BeginInvoke(() => owner.AutomationValueCommitted?.Invoke(owner, EventArgs.Empty));
        }
    }
}
