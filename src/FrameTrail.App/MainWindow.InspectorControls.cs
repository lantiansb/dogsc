using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private readonly Dictionary<string,bool> inspectorDisclosures=[];
 private void ChoiceCards<T>(string label,T current,IEnumerable<(T Value,string Label,string Icon)> items,Action<T> apply,int columns=3,Func<T,FrameworkElement>? preview=null) where T:notnull
 {
  if(label.Length>0)InspectorHost.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush("Secondary"),Margin=new(0,12,0,8)});
  var grid=new UniformGrid{Columns=columns,Margin=new(-3,0,-3,8)};
  foreach(var item in items)
  {
   var selected=EqualityComparer<T>.Default.Equals(current,item.Value);var content=new StackPanel{HorizontalAlignment=HorizontalAlignment.Stretch};
   var visual=preview?.Invoke(item.Value)??new EditorIcon{Kind=item.Icon,Width=24,Height=24,HorizontalAlignment=HorizontalAlignment.Center};visual.Margin=new(0,3,0,7);content.Children.Add(visual);
   content.Children.Add(new TextBlock{Text=item.Label,FontSize=13,FontWeight=selected?FontWeights.Medium:FontWeights.Normal,Foreground=Brush(selected?"Ink":"Secondary"),HorizontalAlignment=HorizontalAlignment.Center,TextWrapping=TextWrapping.Wrap});
   var button=new Button{Content=content,HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new(3),Padding=new(7,9,7,8),Background=Brush(selected?"Chrome":"ControlSurface"),BorderBrush=Brush("Hairline"),BorderThickness=new(.75),ToolTip=item.Label};
   AutomationProperties.SetName(button,item.Label);AutomationProperties.SetItemStatus(button,selected?"已选择":"未选择");button.Click+=(_,_)=>{if(selected)return;CommitInspectorAction((label.Length>0?"修改"+label:"选择"+item.Label),()=>apply(item.Value));};grid.Children.Add(button);
  }
  InspectorHost.Children.Add(grid);
 }
 private void InspectorDisclosure(string title,Action build,bool expanded=false)
 {
  var key=inspectorTab+":"+title;var begin=InspectorHost.Children.Count;build();var children=InspectorHost.Children.Cast<UIElement>().Skip(begin).ToArray();var content=new StackPanel{Margin=new(0,6,0,8)};
  foreach(var child in children){InspectorHost.Children.Remove(child);content.Children.Add(child);}
  var control=new Expander{Header=title,Content=content,IsExpanded=inspectorDisclosures.GetValueOrDefault(key,expanded),Margin=new(0,14,0,2),HorizontalContentAlignment=HorizontalAlignment.Stretch};
  AutomationProperties.SetName(control,title);control.Expanded+=(_,_)=>inspectorDisclosures[key]=true;control.Collapsed+=(_,_)=>inspectorDisclosures[key]=false;InspectorHost.Children.Add(control);
 }
}

public partial class MainWindow
{
    /// <summary>Mac EditorSegmentedControl: a row of mutually exclusive options; re-selecting is a no-op.</summary>
    private void Segmented<T>(string label, T current, IEnumerable<(T Value, string Label)> items, Action<T> apply, string? undo = null) where T : notnull
    {
        if (label.Length > 0)
            InspectorHost.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush("Secondary"), Margin = new(0, 12, 0, 6) });
        var options = items.ToArray();
        var grid = new UniformGrid { Columns = Math.Max(1, options.Length) };
        foreach (var option in options)
        {
            var selected = EqualityComparer<T>.Default.Equals(option.Value, current);
            var button = new Button
            {
                Content = option.Label, Padding = new(6, 7, 6, 7), Margin = new(2), FontSize = 12,
                Background = selected ? Brush("Chrome") : Brushes.Transparent,
                BorderThickness = new(selected ? .5 : 0),
                Foreground = Brush(selected ? "Ink" : "Secondary"),
                FontWeight = selected ? FontWeights.Medium : FontWeights.Normal
            };
            AutomationProperties.SetName(button, (label.Length > 0 ? label + " " : "") + option.Label);
            AutomationProperties.SetItemStatus(button, selected ? "已选择" : "未选择");
            var value = option.Value;
            button.Click += (_, _) =>
            {
                if (selected) return;
                CommitInspectorAction(undo ?? (label.Length > 0 ? "修改" + label : "选择" + option.Label), () => apply(value));
            };
            grid.Children.Add(button);
        }
        InspectorHost.Children.Add(new Border { Background = Brush("ControlSurface"), CornerRadius = new(11), Padding = new(2), Child = grid, Margin = new(0, 4, 0, 8) });
    }

    /// <summary>Mac EditorInspectorEmptyState: nothing selected, so offer to create or pick something.</summary>
    private void InspectorEmptyState(string title, string detail, string icon, params (string Label, Action Action, bool Primary)[] actions)
    {
        InspectorHost.Children.Add(new EditorIcon { Kind = icon, Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 28, 0, 12), Stroke = Brush("Secondary") });
        InspectorHost.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 6) });
        InspectorHost.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Brush("Secondary"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new(0, 0, 0, 14) });
        foreach (var action in actions) ActionButton(action.Label, action.Action, action.Primary);
    }

    /// <summary>Selects an existing effect (timeline + inspector) without moving the playhead.</summary>
    private void SelectEffect(string kind, Guid id)
    {
        selectedEffect = new(kind, id);
        selectedScreenSegmentId = null;
        TimelineView.Selection = selectedEffect;
        TimelineView.MultiSelection.Clear();
        TimelineView.InvalidateVisual();
        BuildInspector();
        RefreshPreviewSelection();
    }

    private void SelectAtPlayheadButton(string kind, string label, Guid? id)
    {
        if (id is not { } target) return;
        ActionButton(label, () => SelectEffect(kind, target));
    }

    /// <summary>Destructive button at the end of an effect panel (Mac editorDestructive "删除片段").</summary>
    private void DeleteEffectButton(string kind, Guid id, string text, string undo)
    {
        var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new(0, 18, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(196, 58, 58)) };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) =>
        {
            CommitInspectorAction(undo, () => InspectorEdit(x => DeleteSelected(x, new TimelineSelection(kind, id))));
            selectedEffect = null;
            TimelineView.Selection = null;
            TimelineView.MultiSelection.Clear();
            TimelineView.InvalidateVisual();
            BuildInspector();
            RefreshPreviewSelection();
        };
        InspectorHost.Children.Add(button);
    }

    /// <summary>Free interval around a clip on a single-lane track (neighbour repair for Start/Length edits).</summary>
    private static (double Lo, double Hi) FreeSpan(IEnumerable<(double Start, double End)> others, double start, double end)
    {
        var lo = 0d;
        var hi = double.PositiveInfinity;
        foreach (var other in others)
        {
            if (other.End <= start + 1e-6) lo = Math.Max(lo, other.End);
            else if (other.Start >= end - 1e-6) hi = Math.Min(hi, other.Start);
        }
        return (lo, hi);
    }
}
