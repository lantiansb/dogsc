using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>
/// Quick reference of every editor shortcut and gesture, grouped like Mac EditorShortcutCheatsheet.swift
/// (播放与浏览 / 剪辑 / 运镜与轨道 / 通用). Command key becomes Ctrl, Option becomes Alt.
/// </summary>
internal sealed class ShortcutCheatsheetWindow : Window
{
    private sealed record Entry(string Keys, string Action);
    private sealed record Group(string Title, Entry[] Entries);

    private static readonly Group[] Groups =
    [
        new("播放与浏览",
        [
            new("空格", "播放 / 暂停"),
            new("← / →", "向前 / 向后移动 1 帧"),
            new("Shift + ← / →", "向前 / 向后移动 5 帧"),
            new("滚轮", "以指针为锚缩放时间线（轨道溢出时用 Ctrl + 滚轮）"),
            new("Shift + 滚轮", "横向滚动时间线"),
            new("拖动标尺", "移动播放头（只有标尺会拖动播放头）"),
            new("点击标尺上的标记", "跳转到该录制标记，右键可删除"),
        ]),
        new("剪辑",
        [
            new("S", "在播放头分割选中动画；未选动画时分割主片段"),
            new("Q", "修剪动画起点；未选动画时波纹删除前段"),
            new("W", "修剪动画终点；未选动画时波纹删除后段"),
            new("D / Delete", "删除当前选中项"),
            new("Shift / Ctrl + 点击", "连续选择 / 增减选择片段"),
            new("Alt + 点击主片段", "在点击处直接切分"),
            new("Ctrl + 拖动主片段右端", "自由变速"),
            new("Shift（拖动时）", "临时关闭吸附"),
            new("恢复剪辑模式", "在标尺上合并或还原剪切缝"),
            new("I / O", "设置导出起点 / 终点"),
        ]),
        new("运镜与轨道",
        [
            new("Ctrl + C / Ctrl + V", "复制效果片段 / 在鼠标所指位置粘贴副本"),
            new("Ctrl + Shift + V", "向选中的同类组件粘贴属性，保留位置与时长"),
            new("单击或拖动效果轨空白", "创建缩放、屏幕 3D、摄像运动、柔化或贴图"),
            new("点击空白处", "取消选择"),
            new("双击分栏柄", "恢复时间线默认高度"),
        ]),
        new("通用",
        [
            new("Ctrl + Z", "撤销"),
            new("Ctrl + Shift + Z / Ctrl + Y", "重做"),
            new("Ctrl + S", "保存项目"),
            new("Ctrl + O", "打开项目"),
            new("Ctrl + E", "导出成片"),
            new("Alt + X", "导出选中的主片段范围"),
            new("Ctrl + Alt + I", "显示 / 隐藏检查器"),
            new("Ctrl + ,", "录制与界面设置"),
            new("F1", "快捷键速查"),
            new("Esc", "取消裁切 / 中止时间线拖动"),
        ]),
    ];

    public ShortcutCheatsheetWindow(Window? owner)
    {
        Title = "快捷键速查";
        Width = 560;
        Height = 560;
        MinHeight = 320;
        ShowInTaskbar = false;
        SurfaceWindow.Configure(this, owner, closeOnEscape: true, resizable: true);
        Content = SurfaceWindow.WrapContent(this, BuildBody(), "快捷键速查");
    }

    private UIElement BuildBody()
    {
        var keyWidth = 190d;
        var stack = new StackPanel { Margin = new Thickness(20, 8, 20, 18) };
        foreach (var group in Groups)
        {
            var heading = new TextBlock { Text = group.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
            stack.Children.Add(heading);
            foreach (var entry in group.Entries)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var key = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    BorderThickness = new Thickness(.75),
                    Child = new TextBlock { Text = entry.Keys, FontSize = 11, FontWeight = FontWeights.Medium, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI") }
                };
                key.SetResourceReference(Border.BackgroundProperty, "ControlSurface");
                key.SetResourceReference(Border.BorderBrushProperty, "Hairline");
                var action = new TextBlock { Text = entry.Action, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                action.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
                Grid.SetColumn(action, 1);
                row.Children.Add(key);
                row.Children.Add(action);
                stack.Children.Add(row);
            }
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
    }
}
