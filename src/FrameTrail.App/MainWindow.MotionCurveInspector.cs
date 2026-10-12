using System.Windows;

namespace FrameTrail.App;

public partial class MainWindow
{
    private void MotionCurveControls(ZoomEasing easing, ZoomBezierCurve curve, Action<ZoomBezierCurve> apply)
    {
        if (easing == ZoomEasing.Custom)
        {
            var chart = new MotionCurveEditor { Curve = curve, Margin = new Thickness(0, 8, 0, 4) };
            chart.CurveCommitted += apply;
            InspectorHost.Children.Add(chart);
            InspectorDisclosure("曲线控制点", () =>
            {
                Number("起点控制 X", curve.X1, 0, 1, value => apply(curve with { X1 = value }), NumFormat.Decimal2, log: false);
                Number("起点控制 Y", curve.Y1, 0, 1, value => apply(curve with { Y1 = value }), NumFormat.Decimal2, log: false);
                Number("终点控制 X", curve.X2, 0, 1, value => apply(curve with { X2 = value }), NumFormat.Decimal2, log: false);
                Number("终点控制 Y", curve.Y2, 0, 1, value => apply(curve with { Y2 = value }), NumFormat.Decimal2, log: false);
                ActionButton("恢复三次曲线", () => apply(ZoomBezierCurve.Cubic));
            });
        }
        if (easing == ZoomEasing.Spring)
        {
            InspectorDisclosure("弹簧参数", () =>
            {
                var spring = project!.Settings.Spring;
                Note("这些参数作用于本项目所有使用弹簧的动画。");
                Number("弹簧质量", spring.Mass, .1, 10, value => InspectorEdit(p => p.Settings.Spring.Mass = value), NumFormat.Decimal1, log: false, undo: "调整弹簧质量");
                Number("弹簧刚度", spring.Stiffness, 1, 1000, value => InspectorEdit(p => p.Settings.Spring.Stiffness = value), NumFormat.Pixels, log: false, undo: "调整弹簧刚度");
                Number("弹簧阻尼", spring.Damping, .1, 100, value => InspectorEdit(p => p.Settings.Spring.Damping = value), NumFormat.Decimal1, log: false, undo: "调整弹簧阻尼");
                ActionButton("恢复默认弹簧", () => InspectorEdit(p => p.Settings.Spring = new SpringAnimationSettings()));
            });
        }
    }
}
