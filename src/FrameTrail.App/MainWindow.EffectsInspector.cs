using System.IO;
using System.Windows;
using Microsoft.Win32;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private void AddMotionClip()=>CreateEffectClip("motion",Playhead.Value);
 private void AddManualZoom()=>CreateEffectClip("zoom",Playhead.Value);
 private static IEnumerable<(ZoomEasing,string)> Easings()=>new[]{(ZoomEasing.Linear,"线性"),(ZoomEasing.Quadratic,"二次"),(ZoomEasing.Cubic,"平滑"),(ZoomEasing.Quintic,"柔和"),(ZoomEasing.Spring,"弹簧"),(ZoomEasing.Custom,"自定义")};
 private void BuildMotionInspector()
 {
  var p=project!;Section("运镜类型");Tabs(motionTab,("zoom","⌕  缩放",()=>{motionTab="zoom";BuildInspector();}),("motion","⬡  屏幕 3D",()=>{motionTab="motion";BuildInspector();}));
  if(motionTab=="zoom")
  {
   var z=p.ZoomClips.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.ZoomClips.FirstOrDefault(x=>SourceNow>=x.Start&&SourceNow<=x.End);
   ActionButton("根据点击生成自动运镜",()=>AutoZoomClick(this,new()));ActionButton("添加手动缩放",AddManualZoom);
   if(z==null){Note("选择缩放片段以调整构图与动画。录制鼠标后可以根据点击自动生成。",true);return;}
   var id=z.Id;ZoomClip Target(FrameTrailProject x)=>x.ZoomClips.First(a=>a.Id==id);
   Section("构图");PointPad("缩放焦点",z.Automatic?ToCropLocalFocus(p,z.Focus):z.Focus,v=>ApplyEdit(x=>{Target(x).Focus=v;Target(x).Automatic=false;}),centered:true);Number("目标大小",z.Scale,1,6,v=>ApplyEdit(x=>Target(x).Scale=v),"×",inline:true);Toggle("点击画面调整焦点",ManualFocus.IsChecked==true,v=>ManualFocus.IsChecked=v);
   Section("片段与动画");Number("开始",z.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>{var a=Target(x);var d=a.End-a.Start;a.Start=v;a.End=Math.Min(x.Media.Duration,v+d);})," s");Number("长度",z.End-z.Start,.1,Math.Max(.1,p.Media.Duration-z.Start),v=>ApplyEdit(x=>Target(x).End=Target(x).Start+v)," s");Number("进入",z.EnterDuration,0,5,v=>ApplyEdit(x=>{Target(x).EnterDuration=v;Target(x).EnterProgressOffset=0;})," s");Number("退出",z.ExitDuration,0,5,v=>ApplyEdit(x=>{Target(x).ExitDuration=v;Target(x).ExitProgressOffset=0;})," s");Choices("曲线",z.Easing,Easings(),v=>ApplyEdit(x=>Target(x).Easing=v));MotionCurveControls(z.Easing,z.CustomCurve,v=>ApplyEdit(x=>Target(x).CustomCurve=v));
   Section("聚焦虚化");Toggle("启用聚焦",z.FocusEffect!=null,v=>ApplyEdit(x=>Target(x).FocusEffect=v?new():null));
   if(z.FocusEffect is { } zoomFocus){FocusEffect F(FrameTrailProject x)=>Target(x).FocusEffect!;Choices("形状",zoomFocus.Shape,new[]{(FocusEffectShape.Radial,"径向"),(FocusEffectShape.Linear,"线性")},v=>ApplyEdit(x=>F(x).Shape=v));Choices("目标",zoomFocus.Target,new[]{(FocusEffectTarget.Animation,"随运镜"),(FocusEffectTarget.Pointer,"随鼠标"),(FocusEffectTarget.Fixed,"固定")},v=>ApplyEdit(x=>F(x).Target=v));PointPad("焦点",zoomFocus.Center,v=>ApplyEdit(x=>F(x).Center=v));Number("范围",zoomFocus.Size,.1,1,v=>ApplyEdit(x=>F(x).Size=v));Number("虚化",zoomFocus.Blur,0,1,v=>ApplyEdit(x=>F(x).Blur=v));Number("暗化",zoomFocus.Dimming,0,.75,v=>ApplyEdit(x=>F(x).Dimming=v));Number("柔和度",zoomFocus.Softness,0,1,v=>ApplyEdit(x=>F(x).Softness=v));if(zoomFocus.Shape==FocusEffectShape.Linear)Number("焦面角度",zoomFocus.AngleDegrees,-180,180,v=>ApplyEdit(x=>F(x).AngleDegrees=v),"°");}
   ActionButton("清除自动运镜",()=>ApplyEdit(x=>x.ZoomClips.RemoveAll(a=>a.Automatic)));
  }
  else
  {
   var clip=p.ScreenMotions.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.ScreenMotions.FirstOrDefault(x=>SourceNow>=x.Start&&SourceNow<=x.End);
   if(clip==null){ActionButton("添加屏幕 3D 片段",AddMotionClip);Note("添加或选择屏幕 3D 片段，然后调整空间姿态。",true);return;}var id=clip.Id;ScreenMotionClip Target(FrameTrailProject x)=>x.ScreenMotions.First(a=>a.Id==id);
   Section("构图");PointPad("缩放焦点",clip.Target.Position,v=>ApplyEdit(x=>Target(x).Target.Position=v),centered:true,hint:"放大时画面向焦点的反方向展开；缩小时会向焦点收拢，因此 1× 两侧的移动方向会自然相反。");Number("目标大小",clip.Target.Scale,.3,3,v=>ApplyEdit(x=>Target(x).Target.Scale=v),"×",inline:true);
   Section("空间姿态");PointPad("3D 倾斜",new((clip.Target.RotationY+45)/90,(clip.Target.RotationX+45)/90),v=>ApplyEdit(x=>{Target(x).Target.RotationX=v.Y*90-45;Target(x).Target.RotationY=v.X*90-45;}),centered:true,tilt:true,hint:"上下控制 X · 左右控制 Y");Number("X 倾斜",clip.Target.RotationX,-65,65,v=>ApplyEdit(x=>Target(x).Target.RotationX=v),"°");Number("Y 倾斜",clip.Target.RotationY,-65,65,v=>ApplyEdit(x=>Target(x).Target.RotationY=v),"°");Number("旋转",clip.Target.RotationZ,-180,180,v=>ApplyEdit(x=>Target(x).Target.RotationZ=v),"°");Number("透视",clip.Target.Perspective,0,2,v=>ApplyEdit(x=>Target(x).Target.Perspective=v));
   Section("片段与动画");Number("开始",clip.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>{Target(x).Start=v;Target(x).Duration=Math.Min(Target(x).Duration,x.Media.Duration-v);})," s");Number("长度",clip.Duration,.1,Math.Max(.1,p.Media.Duration-clip.Start),v=>ApplyEdit(x=>Target(x).Duration=v)," s");Number("进入",clip.EnterDuration,0,3,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("回落",clip.ReturnDuration,0,3,v=>ApplyEdit(x=>Target(x).ReturnDuration=v)," s");Choices("曲线",clip.Easing,Easings(),v=>ApplyEdit(x=>Target(x).Easing=v));MotionCurveControls(clip.Easing,clip.CustomCurve,v=>ApplyEdit(x=>Target(x).CustomCurve=v));
   Section("聚焦虚化");Toggle("启用聚焦",clip.Focus!=null,v=>ApplyEdit(x=>Target(x).Focus=v?new():null));
   if(clip.Focus is { } focus){FocusEffect F(FrameTrailProject x)=>Target(x).Focus!;Choices("形状",focus.Shape,new[]{(FocusEffectShape.Radial,"径向"),(FocusEffectShape.Linear,"线性")},v=>ApplyEdit(x=>F(x).Shape=v));Choices("目标",focus.Target,new[]{(FocusEffectTarget.Animation,"随运镜"),(FocusEffectTarget.Pointer,"随鼠标"),(FocusEffectTarget.Fixed,"固定")},v=>ApplyEdit(x=>F(x).Target=v));PointPad("焦点",focus.Center,v=>ApplyEdit(x=>F(x).Center=v));Number("范围",focus.Size,.1,1,v=>ApplyEdit(x=>F(x).Size=v));Number("虚化",focus.Blur,0,1,v=>ApplyEdit(x=>F(x).Blur=v));Number("暗化",focus.Dimming,0,.75,v=>ApplyEdit(x=>F(x).Dimming=v));Number("柔和度",focus.Softness,0,1,v=>ApplyEdit(x=>F(x).Softness=v));if(focus.Shape==FocusEffectShape.Linear)Number("焦面角度",focus.AngleDegrees,-180,180,v=>ApplyEdit(x=>F(x).AngleDegrees=v),"°");}
  }
 }
 private void AddOverlayClick(object sender,RoutedEventArgs e)=>CreateEffectClip("overlay",Playhead.Value);
 private void AddBlurClip()=>CreateEffectClip("blur",Playhead.Value);
 private void RectControls(NormalizedRect rect,Action<NormalizedRect> apply)
 {
  PointPad("位置",new(rect.X+rect.Width/2,rect.Y+rect.Height/2),v=>apply(rect with{X=v.X-rect.Width/2,Y=v.Y-rect.Height/2}));Number("宽度",rect.Width,.02,1,v=>apply(rect with{Width=v}));Number("高度",rect.Height,.02,1,v=>apply(rect with{Height=v}));
 }
 private void BuildOverlayInspector()
 {
  var p=project!;ActionButton("添加图片贴图…",()=>AddOverlayClick(this,new()));var clip=p.Overlays.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.Overlays.LastOrDefault();if(clip==null)return;var id=clip.Id;OverlayClip Target(FrameTrailProject x)=>x.Overlays.First(a=>a.Id==id);Section("贴图布局");Note(System.IO.Path.GetFileName(clip.MediaPath));RectControls(clip.Rect,v=>ApplyEdit(x=>Target(x).Rect=ConstrainRect(v)));Number("旋转",clip.Rotation,-180,180,v=>ApplyEdit(x=>Target(x).Rotation=v),"°");Number("不透明度",clip.Opacity,0,1,v=>ApplyEdit(x=>Target(x).Opacity=v));Number("圆角",clip.CornerRadius,0,.5,v=>ApplyEdit(x=>Target(x).CornerRadius=v));Number("贴图模糊",clip.Blur,0,1,v=>ApplyEdit(x=>Target(x).Blur=v));Number("图层",clip.Layer,-10,10,v=>ApplyEdit(x=>Target(x).Layer=(int)Math.Round(v)),"","0");
  OverlayStyleControls(clip);Section("片段与动画");Number("开始",clip.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>Target(x).Start=v)," s");Number("长度",clip.Duration,.1,p.Media.Duration,v=>ApplyEdit(x=>Target(x).Duration=v)," s");Choices("入场",clip.EnterMotion,Enum.GetValues<OverlayMotion>().Select(x=>(x,MotionName(x))),v=>ApplyEdit(x=>Target(x).EnterMotion=v));Choices("出场",clip.ExitMotion,Enum.GetValues<OverlayMotion>().Select(x=>(x,MotionName(x))),v=>ApplyEdit(x=>Target(x).ExitMotion=v));Number("入场时长",clip.EnterDuration,0,3,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("出场时长",clip.ExitDuration,0,3,v=>ApplyEdit(x=>Target(x).ExitDuration=v)," s");
 }
 private static string MotionName(OverlayMotion x)=>x switch{OverlayMotion.None=>"无",OverlayMotion.Fade=>"淡入淡出",OverlayMotion.Scale=>"缩放",OverlayMotion.SlideUp=>"向上",OverlayMotion.SlideDown=>"向下",OverlayMotion.SlideLeft=>"向左",OverlayMotion.SlideRight=>"向右",OverlayMotion.SlideTopLeft=>"左上",OverlayMotion.SlideTopRight=>"右上",OverlayMotion.SlideBottomLeft=>"左下",_=>"右下"};
 private void BuildBlurInspector()
 {
  var p=project!;ActionButton("添加柔化片段",AddBlurClip);
  var clip=p.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect?.Id)??p.BlurClips.LastOrDefault();if(clip==null)return;
  var id=clip.Id;BlurClip Target(FrameTrailProject x)=>x.BlurClips.First(a=>a.Id==id);
  Section("柔化区域");RectControls(clip.Rect,v=>ApplyEdit(x=>Target(x).Rect=ConstrainRect(v)));
  Choices("处理方式",clip.Kind,new[]{(BlurKind.Blur,"柔化"),(BlurKind.Pixelate,"马赛克"),(BlurKind.Spotlight,"聚光")},v=>ApplyEdit(x=>Target(x).Kind=v));
  if(clip.Kind==BlurKind.Spotlight)Number("区域外压暗",clip.SpotlightDimming,0,.75,v=>ApplyEdit(x=>Target(x).SpotlightDimming=v));
  else Number("强度",clip.Strength,0,1,v=>ApplyEdit(x=>Target(x).Strength=v));
  Number("圆角",clip.CornerRadius,0,.3,v=>ApplyEdit(x=>Target(x).CornerRadius=v));
  Toggle("随源画面缩放和倾斜",clip.SourceSpace,v=>ApplyEdit(x=>Target(x).SourceSpace=v));
  Choices("转场",clip.TransitionStyle is{} curve?(int)curve:-1,new[]{(-1,"标准"),(0,"无"),(1,"线性"),(2,"顺滑")},v=>ApplyEdit(x=>Target(x).TransitionStyle=v<0?null:(RegionTransitionStyle)v));
  Section("片段");Number("开始",clip.Start,0,Math.Max(0,p.Media.Duration-.1),v=>ApplyEdit(x=>Target(x).Start=v)," s");
  Number("长度",clip.Duration,.1,p.Media.Duration,v=>ApplyEdit(x=>Target(x).Duration=v)," s");
  Number("进入",clip.EnterDuration,0,2,v=>ApplyEdit(x=>Target(x).EnterDuration=v)," s");Number("退出",clip.ExitDuration,0,2,v=>ApplyEdit(x=>Target(x).ExitDuration=v)," s");
 }
 private double cameraVisibleOpacity=1;
 private void BuildCameraInspector()
 {
  var p=project!;
  if(p.Camera is not { } camera){InspectorHost.Children.Add(new FrameTrail.App.EditorIcon{Kind="camera",Width=40,Height=40,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,45,0,14),Stroke=Brush("Secondary")});Section("没有摄像头轨");Note("这个项目没有可编辑的摄像头素材。录制时可启用摄像头，也可导入独立视频。",true);ActionButton("导入摄像头素材…",()=>_=Guard(ImportCamera),true);return;}
  var motion=p.CameraMotions.FirstOrDefault(x=>x.Id==selectedEffect?.Id);
  if(motion!=null){BuildCameraMotionInspector(motion);return;}
  Note("基础布局作用于全片；时间线动画可在指定时段覆盖它。");
  Toggle("显示摄像头",camera.Opacity>0,v=>{if(!v&&camera.Opacity>0)cameraVisibleOpacity=camera.Opacity;ApplyEdit(x=>x.Camera!.Opacity=v?cameraVisibleOpacity:0);});
  if(camera.Opacity>0)
  {
   Section("位置与形状");Note("直接在画布中拖动，拖右下角调整大小。");PointPad("摄像头位置",camera.Position,v=>ApplyEdit(x=>x.Camera!.Position=v));Number("摄像头大小",camera.Size*100,8,90,v=>ApplyEdit(x=>x.Camera!.Size=v/100),"%","0");
   var baseLayout=camera.Layout??(camera.Shape==CameraShape.Circle?CameraMotionLayoutKind.Circle:CameraMotionLayoutKind.RoundedRectangle);
   ChoiceCards("形状",baseLayout,CameraLayouts(),v=>ApplyEdit(x=>{var target=x.Camera!;if(target.Layout is null)target.CornerRadius=Math.Clamp(2*target.CornerRadius/Math.Max(target.Size,.001),0,1);target.Layout=v;target.Shape=v==CameraMotionLayoutKind.Circle?CameraShape.Circle:CameraShape.RoundedRectangle;}),3,CameraLayoutPreview);
   if(baseLayout is CameraMotionLayoutKind.RoundedRectangle or CameraMotionLayoutKind.Horizontal or CameraMotionLayoutKind.Vertical or CameraMotionLayoutKind.Original)Number("圆角程度",camera.Layout is null?camera.CornerRadius*200:CameraRoundness(camera)*100,0,100,v=>ApplyEdit(x=>{var target=x.Camera!;target.CornerRadius=target.Layout is null?v/200:v/100;}),"%","0");
   Section("基础外观");Toggle("镜像摄像头",camera.Mirrored,v=>ApplyEdit(x=>x.Camera!.Mirrored=v));Number("描边",camera.BorderWidth,0,18,v=>ApplyEdit(x=>x.Camera!.BorderWidth=v),"","0");Number("不透明度",camera.Opacity*100,0,100,v=>ApplyEdit(x=>x.Camera!.Opacity=v/100),"%","0");Number("缩放联动",camera.ScaleDuringZoom,.1,2,v=>ApplyEdit(x=>x.Camera!.ScaleDuringZoom=v),"×","0.00");Number("阴影",camera.ShadowStrength*100,0,100,v=>ApplyEdit(x=>x.Camera!.ShadowStrength=v/100),"%","0");
   ActionButton("添加摄像头运动",()=>AddCameraMotion(camera));
   InspectorDisclosure("蒙版内取景",()=>{Note("只调整素材在当前形状内的取景，不移动摄像头外框。");RectControls(camera.Crop,v=>ApplyEdit(x=>x.Camera!.Crop=ConstrainRect(v)));ActionButton("恢复完整摄像头画面",()=>ApplyEdit(x=>x.Camera!.Crop=NormalizedRect.Full));});
  }
  else Note("摄像头画面已隐藏，声音仍使用声音面板的独立设置。开启后可调整布局和外观。");
  InspectorDisclosure("素材同步",()=>{if(camera.SyncSegments is {Count:>1} segments)Note($"录制素材含 {segments.Count} 段同步区间；暂停间隙不显示相机画面。");Number("时间偏移",camera.TimelineStart,-10,10,v=>ApplyEdit(x=>CameraTimelineOperations.Move(x.Camera!,v-x.Camera!.TimelineStart))," s");Number("素材起点",camera.SourceStart,0,Math.Max(.04,AvailableAssetDuration(camera.MediaPath,camera.SourceStart+camera.Duration)-.04),ChangeCameraSourceStart," s");});
  InspectorDisclosure("摄像头素材",()=>{Note(System.IO.Path.GetFileName(camera.MediaPath));ActionButton("导入摄像头素材…",()=>_=Guard(ImportCamera));});
 }
 private void AddCameraMotion(CameraTrack camera)=>CreateEffectClip("cameraMotion",Playhead.Value);
 private static IEnumerable<(CameraMotionLayoutKind Value,string Label,string Icon)> CameraLayouts()=>new[]{(CameraMotionLayoutKind.RoundedRectangle,"圆角方形","window"),(CameraMotionLayoutKind.Circle,"圆形","camera"),(CameraMotionLayoutKind.Horizontal,"横向","landscape"),(CameraMotionLayoutKind.Vertical,"竖向","portrait"),(CameraMotionLayoutKind.Original,"原始比例","crop"),(CameraMotionLayoutKind.Fullscreen,"全屏","fullscreen")};
 private System.Windows.FrameworkElement CameraLayoutPreview(CameraMotionLayoutKind layout)
 {
  var ratio=layout switch{CameraMotionLayoutKind.Circle=>1,CameraMotionLayoutKind.Horizontal=>1.5,CameraMotionLayoutKind.Vertical=>.72,CameraMotionLayoutKind.Fullscreen=>1.7,_=>1};
  return new System.Windows.Controls.Border{Width=layout==CameraMotionLayoutKind.Fullscreen?48:Math.Clamp(32*ratio,22,42),Height=layout==CameraMotionLayoutKind.Fullscreen?28:Math.Clamp(30/ratio,20,34),CornerRadius=new(layout==CameraMotionLayoutKind.Circle?16:layout==CameraMotionLayoutKind.Fullscreen?3:8),Background=Brush("Secondary"),HorizontalAlignment=HorizontalAlignment.Center};
 }
 private void BuildCameraMotionInspector(CameraMotionClip motion)
 {
  var p=project!;var id=motion.Id;CameraMotionClip M(FrameTrailProject x)=>x.CameraMotions.First(a=>a.Id==id);var camera=p.Camera!;var selected=motion.TargetLayout??(motion.TargetRoundness is not null?CameraMotionLayoutKind.RoundedRectangle:camera.Layout??(camera.Shape==CameraShape.Circle?CameraMotionLayoutKind.Circle:CameraMotionLayoutKind.RoundedRectangle));Section("摄像头动画");Note("布局、形状、大小和透明度会在进入与回落过程中连续过渡。");ChoiceCards("目标形状",selected,CameraLayouts(),v=>ApplyEdit(x=>M(x).TargetLayout=v),3,CameraLayoutPreview);PointPad("目标位置",motion.TargetPosition,v=>ApplyEdit(x=>M(x).TargetPosition=v));Number("目标大小",motion.TargetSize,.08,.9,v=>ApplyEdit(x=>M(x).TargetSize=v));if(selected is CameraMotionLayoutKind.RoundedRectangle or CameraMotionLayoutKind.Horizontal or CameraMotionLayoutKind.Vertical or CameraMotionLayoutKind.Original)Number("目标圆角",(motion.TargetRoundness??CameraRoundness(camera))*100,0,100,v=>ApplyEdit(x=>M(x).TargetRoundness=v/100),"%","0");Number("目标不透明度",(motion.TargetOpacity??camera.Opacity)*100,0,100,v=>ApplyEdit(x=>M(x).TargetOpacity=v/100),"%","0");Section("片段与动画");Number("时长",motion.Duration,.1,p.Media.Duration,v=>ApplyEdit(x=>M(x).Duration=v)," s");Number("进入",motion.EnterDuration,0,3,v=>ApplyEdit(x=>M(x).EnterDuration=v)," s");Number("回落",motion.ReturnDuration,0,3,v=>ApplyEdit(x=>M(x).ReturnDuration=v)," s");ChoiceCards("曲线",motion.Easing,Easings().Select(x=>(x.Item1,x.Item2,"wave")),v=>ApplyEdit(x=>M(x).Easing=v),2);MotionCurveControls(motion.Easing,motion.CustomCurve,v=>ApplyEdit(x=>M(x).CustomCurve=v));
 }
 private static double CameraRoundness(CameraTrack camera)=>camera.Layout is not null?Math.Clamp(camera.CornerRadius,0,1):Math.Clamp(2*camera.CornerRadius/Math.Max(camera.Size,.001),0,1);
 private async Task ImportCamera()
 {
  if(projectFile==null)return;var d=new OpenFileDialog{Filter="视频|*.mp4;*.mov;*.mkv;*.avi"};if(d.ShowDialog(this)!=true)return;var info=await MediaTools.ProbeAsync(d.FileName);if(info.Width<1||info.Height<1||info.Duration<=0)throw new InvalidDataException("视频没有可用画面。");var relative="media/camera-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(d.FileName);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.Copy(d.FileName,path);auxiliaryDurations[relative]=info.Duration;ApplyEdit(p=>p.Camera=new(){MediaPath=relative,Duration=info.Duration,AssetAspectRatio=info.Width/(double)info.Height});
 }
 private void BuildAudioInspector()
 {
  var p=project!;Section("全片音轨");Note("音量和静音作用于整条素材，剪辑、变速和重排后按最终时间线混合。");
  AudioVolumeRow("系统声音","screen",p.Settings.MainAudioMuted,p.Settings.MainAudioVolume,v=>ApplyEdit(x=>x.Settings.MainAudioMuted=v),v=>ApplyEdit(x=>x.Settings.MainAudioVolume=v));
  if(p.Camera!=null)AudioVolumeRow("摄像头声音","camera",p.Camera.Muted,p.Camera.Volume,v=>ApplyEdit(x=>x.Camera!.Muted=v),v=>ApplyEdit(x=>x.Camera!.Volume=v));
  for(var i=0;i<p.AudioTracks.Count;i++)
  {
   var index=i;var a=p.AudioTracks[i];if(a.Source!=AudioTrackSource.System)AudioVolumeRow(a.Source==AudioTrackSource.Microphone?"麦克风":"独立音轨 "+(i+1),"audio",a.Muted,a.Volume,v=>ApplyEdit(x=>x.AudioTracks[index].Muted=v),v=>ApplyEdit(x=>x.AudioTracks[index].Volume=v));
   InspectorDisclosure($"音轨 {i+1} · 同步与素材",()=>{Note(System.IO.Path.GetFileName(a.MediaPath));Number("时间偏移",a.OffsetSeconds,-10,10,v=>ApplyEdit(x=>AudioTimelineOperations.Move(x.AudioTracks[index],v-x.AudioTracks[index].OffsetSeconds))," s");ActionButton("移除音轨",()=>ApplyEdit(x=>x.AudioTracks.RemoveAt(index)));});
  }
  ActionButton("导入音频…",()=>_=Guard(ImportAudio));
 }
 private void AudioVolumeRow(string title,string icon,bool muted,double volume,Action<bool> mute,Action<double> change)
 {
  var row=new System.Windows.Controls.DockPanel{Margin=new(0,20,0,3)};row.Children.Add(new EditorIcon{Kind=icon,Width=17,Height=17,Margin=new(0,0,8,0),Stroke=Brush("Secondary")});row.Children.Add(new System.Windows.Controls.TextBlock{Text=title,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});InspectorHost.Children.Add(row);Toggle("静音",muted,mute);Number("音量",volume*100,0,200,v=>change(v/100),"%","0");
 }
 private async Task ImportAudio()
 {
  if(projectFile==null)return;var d=new OpenFileDialog{Filter="音频/视频|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.mp4;*.mov"};if(d.ShowDialog(this)!=true)return;var info=await MediaTools.ProbeAsync(d.FileName);if(!info.HasAudio)throw new InvalidDataException("素材没有音轨。");var relative="media/audio-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(d.FileName);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.Copy(d.FileName,path);auxiliaryDurations[relative]=info.Duration;ApplyEdit(p=>p.AudioTracks.Add(new(){MediaPath=relative,Duration=info.Duration}));
 }

}
