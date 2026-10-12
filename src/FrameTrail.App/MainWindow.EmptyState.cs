using System.Windows;
using System.Windows.Controls;

namespace FrameTrail.App;

public partial class MainWindow
{
    /// <summary>
    /// E11: the empty editor (no project loaded, e.g. after the editor window was closed) lists the last few projects so the tray's
    /// "打开编辑器" is not a dead end. Mac shows the recent projects in the setup phase.
    /// </summary>
    private void RefreshEmptyRecents()
    {
        if (EmptyRecents is null) return;
        EmptyRecents.Children.Clear();
        IReadOnlyList<string> recent;
        try { recent = RecentProjects.Read().Take(3).ToArray(); }
        catch (Exception ex) { App.Log(ex); return; }
        if (recent.Count == 0) return;
        EmptyRecents.Children.Add(new TextBlock
        {
            Text = "最近项目", FontSize = 12, Foreground = Brush("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6)
        });
        foreach (var path in recent)
        {
            var file = path;
            var (title, date) = RecentProjects.Describe(file);
            var button = new Button { Content = title + (date.Length > 0 ? "  ·  " + date : ""), Margin = new Thickness(0, 3, 0, 3), MinWidth = 260, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => _ = Guard(async () => { await OpenProject(file); });
            EmptyRecents.Children.Add(button);
        }
    }
}
