using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private readonly ListBox Clips=new(),Markers=new();
 private readonly Slider Playhead=new(){Minimum=0,Maximum=1},PaddingSlider=new(){Maximum=.35},RadiusSlider=new(){Maximum=.2},ShadowSlider=new();
 private readonly Canvas Waveform=new();
 private readonly TextBox BackgroundColor=new(),MouseOffset=new();
 private readonly CheckBox ShowCursor=new(),ManualFocus=new(),HardwareEncoder=new(){IsChecked=true};
 private readonly ComboBox ZoomScale=new(),Resolution=new(),ExportFps=new(),Speed=new();
 private readonly TextBlock TimelineInfo=new();
 private string inspectorTab="frame",frameTab="layout",motionTab="zoom";
 private bool darkTheme,buildingInspector;
 private int previewResolution=0,previewFrameRate=60;
 private bool excludeEditorDuringRecording=true;
 private TimelineSelection? selectedEffect;
 private string? effectClipboardKind,effectClipboardJson;
 private string? preferredRecordingRoot;
 private Brush Brush(string name)=>(Brush)FindResource(name);
 private double SourceNow=>project==null?0:project.Timeline.MapOutputToSource(Math.Clamp(Playhead.Value,0,project.Timeline.Duration));
 private void InitializeEditorShell()
 {
  foreach(var text in new[]{"1.3","1.6","2.0"})ZoomScale.Items.Add(text);ZoomScale.SelectedIndex=1;
  foreach(var text in new[]{"素材","1080p","1440p","4K"})Resolution.Items.Add(text);Resolution.SelectedIndex=1;
  foreach(var text in new[]{"30","60"})ExportFps.Items.Add(text);ExportFps.SelectedIndex=1;
  foreach(var text in new[]{"0.25","0.5","0.75","1","1.5","2","4"})Speed.Items.Add(text);Speed.SelectedIndex=3;
  Playhead.ValueChanged+=PlayheadChanged;Clips.SelectionChanged+=ClipSelected;
  RenderOptions.SetBitmapScalingMode(PreviewImage,BitmapScalingMode.HighQuality);
  PreviewSurface.SizeChanged+=(_,_)=>SchedulePreviewResize();
  DpiChanged+=(_,_)=>SchedulePreviewResize();
  previewResizeTimer.Tick+=(_,_)=>{previewResizeTimer.Stop();if(project!=null&&previewResolution==0&&!playing&&operationCancellation==null)_=Guard(()=>RenderTimelinePreview(Playhead.Value));};
  TimelineView.SeekRequested+=SeekTimeline;
  TimelineView.ScrubRequested+=ScrubTimeline;
  TimelineView.SelectionRequested+=s=>{selectedEffect=s.Kind=="screen"?null:s;selectedScreenSegmentId=s.Kind=="screen"?s.Id:null;if(s.Kind=="screen"){inspectorTab="audio";refreshing=true;Clips.SelectedItem=Clips.Items.Cast<ClipItem>().FirstOrDefault(x=>x.Id==s.Id);refreshing=false;}else {inspectorTab=s.Kind switch{"camera" or "cameraMotion"=>"camera","audio"=>"audio","overlay"=>"overlay","blur"=>"blur",_=>"zoom"};motionTab=s.Kind=="motion"?"motion":"zoom";}BuildInspector();};
  TimelineView.ChangeRequested+=ApplyTimelineChange;
  TimelineView.ZoomRequested+=v=>TimelineZoom.Value=v;
  TimelineView.ScrollRequested+=v=>TimelineScroll.Value=v;
  TimelineView.ContextRequested+=TimelineContextMenu;TimelineView.TrackMenuRequested+=TimelineTracksMenu;
  SnapBoundaries.Checked+=(_,_)=>{TimelineView.Snap=true;};SnapBoundaries.Unchecked+=(_,_)=>{TimelineView.Snap=false;};
  BuildInspector();
 }
 private void RefreshEditorShell()
 {
  if(TimelineView==null)return;MarkerMenuButton.Visibility=project?.Markers.Count>0?Visibility.Visible:Visibility.Collapsed;TimelineView.Project=project;TimelineView.Assets=timelineAssets;TimelineView.Position=Playhead.Value;
  if(selectedEffect!=null)TimelineView.Selection=selectedEffect;else if(Clips.SelectedItem is ClipItem clip)TimelineView.Selection=new("screen",clip.Id);
  UpdateTimelineDisplayButtons();TimelineView.Refresh();TimelineScroll.Maximum=Math.Max(0,TimelineView.DocumentWidth-Math.Max(1,TimelineView.ActualWidth-152));TimelineScroll.ViewportSize=Math.Max(1,TimelineView.ActualWidth-152);
  if(project!=null){var g=Gcd(project.Settings.CanvasWidth,project.Settings.CanvasHeight);AspectLabel.Text=$"{project.Settings.CanvasWidth/g}:{project.Settings.CanvasHeight/g}";}
  BuildInspector();
 }
 private static int Gcd(int a,int b){while(b!=0)(a,b)=(b,a%b);return Math.Max(1,a);}
 private void WorkspaceSizeChanged(object sender,SizeChangedEventArgs e)
 {
  if(InspectorColumn==null)return;var compression=Math.Clamp(Math.Max((1440-ActualWidth)/360,(820-ActualHeight)/220),0,1);InspectorColumn.Width=new GridLength(Math.Clamp(ActualWidth*.23,340,416)*(1-.14*compression));ToolbarRow.Height=new GridLength(92-16*compression);RefreshEditorShell();
 }
 private void MinimizeClick(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
 private void MaximizeClick(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
 private void CloseWindowClick(object sender,RoutedEventArgs e)=>Close();
 private void ToolbarDrag(object sender,MouseButtonEventArgs e){if(e.OriginalSource is Border or Grid or DockPanel){if(e.ClickCount==2)MaximizeClick(sender,e);else DragMove();}}
 private void ShowRecorderClick(object sender,RoutedEventArgs e)=>_=Guard(ActivateDesktopRecorderAsync);
 private void HideRecorderClick(object sender,RoutedEventArgs e)=>RecorderOverlay.Visibility=Visibility.Collapsed;
 private void RecorderModeClick(object sender,RoutedEventArgs e)=>CaptureKind.SelectedIndex=int.Parse((string)((Button)sender).Tag);
 private void RecordingFolderClick(object sender,RoutedEventArgs e){var d=new OpenFolderDialog{Title="选择录制保存目录"};if(d.ShowDialog(DesktopDialogOwner())==true){preferredRecordingRoot=d.FolderName;RefreshRecorderSavePathDisplay();}}
 private void InspectorTabClick(object sender,RoutedEventArgs e){inspectorTab=(string)((Button)sender).Tag;BuildInspector();}
 private void CanvasControlsClick(object sender,RoutedEventArgs e){inspectorTab="frame";frameTab="layout";BuildInspector();}
 private void CropClick(object sender,RoutedEventArgs e){inspectorTab="crop";BuildInspector();}
 private void FilmstripClick(object sender,RoutedEventArgs e){TimelineView.Filmstrip=!TimelineView.Filmstrip;UpdateTimelineDisplayButtons();TimelineView.InvalidateVisual();}
 private void WaveClick(object sender,RoutedEventArgs e){TimelineView.Waveform=!TimelineView.Waveform;UpdateTimelineDisplayButtons();TimelineView.InvalidateVisual();}
 private void UpdateTimelineDisplayButtons(){FilmstripButton.SetResourceReference(BackgroundProperty,TimelineView.Filmstrip?"ControlSurface":"Surface");WaveButton.SetResourceReference(BackgroundProperty,TimelineView.Waveform?"ControlSurface":"Surface");}
 private void FitTimelineClick(object sender,RoutedEventArgs e){TimelineZoom.Value=1;TimelineScroll.Value=0;}
 private void ZoomInTimelineClick(object sender,RoutedEventArgs e)=>TimelineZoom.Value=Math.Min(12,TimelineZoom.Value*1.25);
 private void ZoomOutTimelineClick(object sender,RoutedEventArgs e)=>TimelineZoom.Value=Math.Max(1,TimelineZoom.Value/1.25);
 private void TimelineZoomChanged(object sender,RoutedPropertyChangedEventArgs<double> e){if(TimelineView==null)return;TimelineView.Zoom=e.NewValue;RefreshEditorShell();}
 private void TimelineScrollChanged(object sender,RoutedPropertyChangedEventArgs<double> e){if(TimelineView==null)return;TimelineView.Offset=e.NewValue;TimelineView.InvalidateVisual();}
 private void ApplyEdit(Action<FrameTrailProject> action){if(buildingInspector)return;_=Guard(()=>{Edit(action);return Task.CompletedTask;});}
 private void ThemeClick(object sender,RoutedEventArgs e)
 {
  darkTheme=!darkTheme;var colors=darkTheme?new[]{"#252829","#303334","#393D3E","#F1F2F2","#A6ABAD","#484D4F","#3B3F40","#35584B","#62B693","#4B5052"}:new[]{"#F4F6F6","#FCFCFC","#FFFFFF","#303232","#707579","#E6E7E8","#F2F2F4","#E1F2EA","#46A986","#D9DDDC"};var keys=new[]{"Workspace","Surface","Chrome","Ink","Secondary","Hairline","ControlSurface","Selection","Accent","Dot"};for(var i=0;i<keys.Length;i++)Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));TimelineView.InvalidateVisual();BuildInspector();
 }
}
