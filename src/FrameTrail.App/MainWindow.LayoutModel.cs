using System.Windows;

namespace FrameTrail.App;

/// <summary>
/// Screen-card layout version (FrameTrailSettings.LayoutModel). Projects saved before the Mac card model keep their original
/// geometry; this partial offers the explicit, undoable switch to the Mac model (zoom magnifies the whole card, positions are
/// resolved through placement/focus framing).
/// </summary>
public partial class MainWindow
{
    private bool CanUpgradeLayoutModel => project is not null && project.Settings.LayoutModel < FrameTrailSettings.MacLayoutModel;

    /// <summary>Project menu entry; hidden once the project already uses the Mac model.</summary>
    private void AddUpgradeLayoutModelMenu(System.Windows.Controls.ItemsControl menu)
    {
        if (!CanUpgradeLayoutModel) return;
        AddMenu(menu, "升级到 Mac 画面语义（缩放整卡放大）…", () => UpgradeLayoutModelClick());
    }

    private void UpgradeLayoutModelClick()
    {
        if (!CanUpgradeLayoutModel) return;
        var answer = SurfaceDialog.Show(this,
            "升级后，缩放会整张卡片一起放大（而不是在卡片内部裁切），屏幕运镜位置与 3D 倾斜支点按 Mac 版规则计算，"
            + "已有的缩放、运镜和画布裁切的构图可能与现在略有不同。\n\n此操作可以撤销（Ctrl+Z）。是否升级？",
            "升级画面语义", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _ = Guard(() =>
        {
            Edit(p => p.Settings.LayoutModel = FrameTrailSettings.MacLayoutModel, "升级画面语义");
            ShowNotice("已升级到 Mac 画面语义。");
            return Task.CompletedTask;
        });
    }
}
