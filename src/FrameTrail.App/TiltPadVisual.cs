using System.Windows;
using System.Windows.Media;
namespace FrameTrail.App;

/// <summary>
/// Perspective card inside the 3D tilt pad. Mirrors Mac EditorTiltPad's rotation3DEffect: the card
/// follows 55% of the real X/Y tilt in degrees; a positive X tilt (handle up) tips the top edge away.
/// </summary>
public sealed class TiltPadVisual : FrameworkElement
{
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public TiltPadVisual() { IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var rx = RotationX * .55 * Math.PI / 180;
        var ry = RotationY * .55 * Math.PI / 180;
        // Unit card of the Mac pad: 24pt side margins, 18pt top/bottom margins.
        var halfWidth = Math.Max(1, ActualWidth / 2 - 24);
        var halfHeight = Math.Max(1, ActualHeight / 2 - 18);
        Point Project(double x, double y)
        {
            var z = -y * Math.Sin(rx) + x * Math.Sin(ry);
            var px = x * Math.Cos(ry);
            var py = y * Math.Cos(rx);
            var k = 1 / (1 + z * .72 * .5);
            return new(ActualWidth / 2 + px * k * halfWidth, ActualHeight / 2 + py * k * halfHeight);
        }
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(Project(-1, -1), true, true);
            c.LineTo(Project(1, -1), true, false);
            c.LineTo(Project(1, 1), true, false);
            c.LineTo(Project(-1, 1), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(90, 75, 77, 79)), 1.2), geometry);
    }
}
