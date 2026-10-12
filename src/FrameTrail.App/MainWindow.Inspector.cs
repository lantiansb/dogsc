using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private void BuildInspector()
 {
  if(InspectorHost==null||buildingInspector)return;
  // A control is mid drag/typing: rebuild once it commits so the control is not destroyed under the pointer.
  if(inspectorTransactions>0){inspectorRebuildDeferred=true;return;}
  inspectorRebuildDeferred=false;buildingInspector=true;string? focusKey=null;
  try
  {
   if(selectedScreenSegmentId is { } staleSegment&&(project==null||project.Timeline.Segments.All(x=>x.Id!=staleSegment)))selectedScreenSegmentId=null;
   if(!releaseFocusAfterCommit)focusKey=CapturedInspectorFocusKey();
   RememberInspectorScroll();
   InspectorHost.Children.Clear();inspectorFocusTargets.Clear();inspectorKeyCounts.Clear();InspectorBadge.Visibility=inspectorTab=="zoom"&&selectedEffect?.Kind=="motion"?Visibility.Visible:Visibility.Collapsed;
   InspectorTitle.Text=inspectorTab switch{"opening"=>"开场","zoom"=>"运镜","cursor"=>"光标","camera"=>"摄像头","audio"=>"声音","crop"=>"裁切","overlay"=>"贴图","blur"=>"柔化",_=>"画面"};
   UpdateInspectorScope();UpdateInspectorRail();
   if(project==null){Note("打开项目后，在这里调整画面与效果。",true);return;}
   switch(inspectorTab){case "frame":BuildFrameInspector();break;case "zoom":BuildMotionPanel();break;case "cursor":BuildCursorInspector();break;case "camera":BuildCameraInspector();break;case "audio":if(selectedScreenSegmentId is { } segmentId)BuildSegmentAudioInspector(segmentId);else BuildAudioInspector();break;case "crop":BuildCropInspector();break;case "opening":BuildOpeningInspector();break;case "overlay":BuildOverlayInspector();break;case "blur":BuildBlurInspector();break;}
  }
  finally{buildingInspector=false;RefreshPreviewSelection();RestoreInspectorScroll();RestoreInspectorFocus(focusKey);}
 }
 private void Section(string text)
 {
  var title=new TextBlock{Text=text,Style=(Style)FindResource("SectionTitle")};if(inspectorTab=="zoom"&&motionTab=="motion")title.Margin=new Thickness(0,20,0,12);
  if(text!="圆角、描边与阴影"){InspectorHost.Children.Add(title);return;}
  var row=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=title.Margin};
  row.Children.Add(new EditorIcon{Kind="squareStack",Width=14,Height=14,Stroke=Brush("Secondary"),Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center});title.Margin=new Thickness(0);row.Children.Add(title);InspectorHost.Children.Add(row);
 }
 private void Note(string text,bool spacious=false)=>InspectorHost.Children.Add(new TextBlock{Text=text,Foreground=Brush("Secondary"),FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,spacious?24:8,0,12)});
 private void ActionButton(string label,Action action,bool primary=false){var b=new Button{Content=label,HorizontalContentAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,6,0,4)};if(primary)b.Style=(Style)FindResource("PrimaryButton");b.Click+=(_,_)=>CommitInspectorAction(label.TrimEnd('…'),action);InspectorHost.Children.Add(b);}
 private void Toggle(string label,bool current,Action<bool> action){var c=new CheckBox{Content=label,IsChecked=current,Style=(Style)FindResource("InspectorToggle")};RoutedEventHandler changed=(_,_)=>{if(!buildingInspector)CommitInspectorAction("切换"+label,()=>action(c.IsChecked==true));};c.Checked+=changed;c.Unchecked+=changed;InspectorHost.Children.Add(c);}
 private void TextField(string label,string text,Action<string> apply,int maxLength=0)
 {
  if(label.Length>0)InspectorHost.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush("Secondary"),Margin=new Thickness(0,12,0,4)});
  var box=new TextBox{Text=text,MaxLength=maxLength};var previous=text;void Commit(){if(buildingInspector||box.Text==previous)return;previous=box.Text;CommitInspectorAction("修改"+(label.Length>0?label:"文字"),()=>apply(box.Text));}box.LostKeyboardFocus+=(_,_)=>Commit();box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();FocusEditor();e.Handled=true;}else if(e.Key==Key.Escape){box.Text=previous;FocusEditor();e.Handled=true;}};InspectorHost.Children.Add(box);
 }
 private void Choices<T>(string label,T value,IEnumerable<(T Value,string Label)> items,Action<T> apply) where T:notnull
 {
  if(label.Length>0)InspectorHost.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,3),Foreground=Brush("Secondary"),FontSize=12});var array=items.ToArray();var combo=new ComboBox{ItemsSource=array.Select(x=>x.Label).ToArray(),SelectedIndex=Array.FindIndex(array,x=>EqualityComparer<T>.Default.Equals(x.Value,value))};System.Windows.Automation.AutomationProperties.SetName(combo,label.Length>0?label:"背景类型");combo.SelectionChanged+=(_,_)=>{if(!buildingInspector&&combo.SelectedIndex>=0)CommitInspectorAction("修改"+(label.Length>0?label:"背景类型"),()=>apply(array[combo.SelectedIndex].Value));};InspectorHost.Children.Add(combo);
 }
 private void Tabs(string selected,params (string Key,string Text,Action Action)[] items)
 {
  var grid=new UniformGrid{Columns=items.Length};foreach(var item in items){var label=item.Text.Contains("  ")?item.Text[(item.Text.IndexOf("  ",StringComparison.Ordinal)+2)..]:item.Text;var content=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};content.Children.Add(new EditorIcon{Kind=item.Key switch{"background"=>"layers","layout"=>"aspect","frame"=>"window","motion"=>"cube",_=>"zoom"},Width=13,Height=13,Stroke=Brush(item.Key==selected?"Ink":"Secondary"),Margin=new(0,0,6,0)});content.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush(item.Key==selected?"Ink":"Secondary"),VerticalAlignment=VerticalAlignment.Center});var b=new Button{Content=content,Padding=new Thickness(6,8,6,8),Margin=new Thickness(2),Background=item.Key==selected?Brush("Chrome"):Brushes.Transparent,BorderThickness=new Thickness(item.Key==selected?.5:0)};System.Windows.Automation.AutomationProperties.SetName(b,item.Text);b.Click+=(_,_)=>item.Action();grid.Children.Add(b);}InspectorHost.Children.Add(new Border{Background=Brush("ControlSurface"),CornerRadius=new CornerRadius(11),Padding=new Thickness(2),Child=grid,Margin=new Thickness(-5,0,-5,8)});
 }
 private static CanvasSettings CanvasSettingsFor(FrameTrailProject p)=>p.Settings.Canvas??=new(){AspectWidth=p.Settings.CanvasWidth,AspectHeight=p.Settings.CanvasHeight};
 private static BackgroundSettings BackgroundSettingsFor(FrameTrailProject p)=>p.Settings.BackgroundStyle??=new(){Color=p.Settings.Background,EndColor=p.Settings.Background};
 private static CursorSettings CursorSettingsFor(FrameTrailProject p)=>p.Settings.Cursor??=new(){Visible=p.Settings.CursorVisible};
 private void BuildFrameInspector()
 {
  var p=project!;Tabs(frameTab,("background","▱  背景",()=>{frameTab="background";BuildInspector();}),("layout","▣  布局",()=>{frameTab="layout";BuildInspector();}),("frame","▢  样机",()=>{frameTab="frame";BuildInspector();}));
  var canvas=p.Settings.Canvas??new CanvasSettings();
  if(frameTab=="layout")
  {
   var height=p.Settings.CanvasHeight;
   Section("画面留白");Number("边距",p.Settings.Padding*height,0,height*.35,v=>InspectorEdit(x=>x.Settings.Padding=v/x.Settings.CanvasHeight),NumFormat.Pixels,log:false);
   Section("位置与大小");PointPad("素材位置",canvas.ContentPosition,v=>InspectorEdit(x=>CanvasSettingsFor(x).ContentPosition=v));Number("素材缩放",canvas.ContentScale,.25,4,v=>InspectorEdit(x=>CanvasSettingsFor(x).ContentScale=v),NumFormat.Multiplier);
   var restoreRow=new DockPanel{Margin=new(0,15,0,8)};var restore=new Button{Content="恢复位置与大小",Style=(Style)FindResource("PlainButton"),Padding=new(0),FontSize=11};DockPanel.SetDock(restore,Dock.Right);restore.Click+=(_,_)=>CommitInspectorAction("恢复位置与大小",()=>InspectorEdit(x=>{CanvasSettingsFor(x).ContentPosition=new(.5,.5);CanvasSettingsFor(x).ContentScale=1;}));restoreRow.Children.Add(restore);restoreRow.Children.Add(new TextBlock{Text="可以直接在画面中拖动",FontSize=11,Foreground=Brush("Secondary"),VerticalAlignment=VerticalAlignment.Center});InspectorHost.Children.Add(restoreRow);
   Section("圆角、描边与阴影");Number("内容圆角",p.Settings.CornerRadius*height,0,height*.2,v=>InspectorEdit(x=>x.Settings.CornerRadius=v/x.Settings.CanvasHeight),NumFormat.Pixels,log:false);Number("外描边",canvas.BorderWidth,0,60,v=>InspectorEdit(x=>CanvasSettingsFor(x).BorderWidth=v),NumFormat.Pixels,log:false);ColorField("描边颜色",canvas.BorderColor,v=>InspectorEdit(x=>CanvasSettingsFor(x).BorderColor=v),"调整描边颜色");Number("阴影",p.Settings.Shadow,0,1,v=>InspectorEdit(x=>x.Settings.Shadow=v),NumFormat.Percent);
  }
  else if(frameTab=="background") BuildBackgroundInspector();
  else
  {
   BuildMockupInspector();
  }
 }
 private static NormalizedRect ConstrainRect(NormalizedRect r){var width=Math.Clamp(r.Width,.01,1);var height=Math.Clamp(r.Height,.01,1);return new(Math.Clamp(r.X,0,1-width),Math.Clamp(r.Y,0,1-height),width,height);}
 private void BuildCropInspector()
 {
  var c=project!.Settings.Canvas?.Crop??NormalizedRect.Full;Section("源画面裁切");Note("按原始素材的归一化区域裁切，导出和预览共用此区域。");
  Number("左侧",c.X*100,0,99,v=>InspectorEdit(p=>{var r=CanvasSettingsFor(p).Crop;CanvasSettingsFor(p).Crop=new(v/100,r.Y,Math.Min(r.Width,1-v/100),r.Height);}),"%");Number("顶部",c.Y*100,0,99,v=>InspectorEdit(p=>{var r=CanvasSettingsFor(p).Crop;CanvasSettingsFor(p).Crop=new(r.X,v/100,r.Width,Math.Min(r.Height,1-v/100));}),"%");Number("宽度",c.Width*100,1,(1-c.X)*100,v=>InspectorEdit(p=>CanvasSettingsFor(p).Crop=CanvasSettingsFor(p).Crop with{Width=v/100}),"%");Number("高度",c.Height*100,1,(1-c.Y)*100,v=>InspectorEdit(p=>CanvasSettingsFor(p).Crop=CanvasSettingsFor(p).Crop with{Height=v/100}),"%");ActionButton("恢复完整画面",()=>InspectorEdit(p=>CanvasSettingsFor(p).Crop=NormalizedRect.Full));
 }
 private void ImportImageAsset(Action<string> imported)
 {
  if(projectFile==null)return;var dialog=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp"};if(dialog.ShowDialog(this)!=true)return;var relative="media/asset-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(dialog.FileName);var target=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);File.Copy(dialog.FileName,target);try{if(!target.EndsWith(".webp",StringComparison.OrdinalIgnoreCase)){using var stream=File.OpenRead(target);var decoder=System.Windows.Media.Imaging.BitmapDecoder.Create(stream,System.Windows.Media.Imaging.BitmapCreateOptions.None,System.Windows.Media.Imaging.BitmapCacheOption.None);if(decoder.Frames.Count==0)throw new InvalidDataException("图片无法解码。");}}catch(Exception ex)when(ex is NotSupportedException or InvalidDataException or IOException or System.IO.FileFormatException){try{File.Delete(target);}catch(IOException){}throw new InvalidDataException("图片无法解码，未导入。",ex);}try{imported(relative);}catch{try{File.Delete(target);}catch(IOException){}throw;}
 }
 private async Task ImportVideoBackground()
 {
  if(projectFile==null)return;var dialog=new OpenFileDialog{Filter="视频|*.mp4;*.mov;*.mkv;*.webm;*.avi"};if(dialog.ShowDialog(this)!=true){BuildInspector();return;}var info=await MediaTools.ProbeAsync(dialog.FileName);if(info.Width<1||info.Duration<=0)throw new InvalidDataException("视频没有可用画面。");var relative="media/background-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(dialog.FileName);var target=FrameTrailProjectStore.ResolveAssetPath(projectFile,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);File.Copy(dialog.FileName,target);ApplyEdit(p=>{var bg=BackgroundSettingsFor(p);bg.Kind=BackgroundKind.Video;bg.VideoPath=relative;bg.VideoStartTime=0;});
 }
}
