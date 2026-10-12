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
  private void AddOverlayClick(object sender,RoutedEventArgs e)=>CreateEffectClip("overlay",Playhead.Value);
  private void AddBlurClip()=>CreateEffectClip("blur",Playhead.Value);
  private void AddCameraMotion(CameraTrack camera)=>CreateEffectClip("cameraMotion",Playhead.Value);
 private static IEnumerable<(CameraMotionLayoutKind Value,string Label,string Icon)> CameraLayouts()=>new[]{(CameraMotionLayoutKind.RoundedRectangle,"圆角方形","window"),(CameraMotionLayoutKind.Circle,"圆形","camera"),(CameraMotionLayoutKind.Horizontal,"横向","landscape"),(CameraMotionLayoutKind.Vertical,"竖向","portrait"),(CameraMotionLayoutKind.Original,"原始比例","crop"),(CameraMotionLayoutKind.Fullscreen,"全屏","fullscreen")};
 private System.Windows.FrameworkElement CameraLayoutPreview(CameraMotionLayoutKind layout)
 {
  var ratio=layout switch{CameraMotionLayoutKind.Circle=>1,CameraMotionLayoutKind.Horizontal=>1.5,CameraMotionLayoutKind.Vertical=>.72,CameraMotionLayoutKind.Fullscreen=>1.7,_=>1};
  return new System.Windows.Controls.Border{Width=layout==CameraMotionLayoutKind.Fullscreen?48:Math.Clamp(32*ratio,22,42),Height=layout==CameraMotionLayoutKind.Fullscreen?28:Math.Clamp(30/ratio,20,34),CornerRadius=new(layout==CameraMotionLayoutKind.Circle?16:layout==CameraMotionLayoutKind.Fullscreen?3:8),Background=Brush("Secondary"),HorizontalAlignment=HorizontalAlignment.Center};
 }
 private static double CameraRoundness(CameraTrack camera)=>camera.Layout is not null?Math.Clamp(camera.CornerRadius,0,1):Math.Clamp(2*camera.CornerRadius/Math.Max(camera.Size,.001),0,1);
 private async Task ImportCamera()
 {
  if(projectFile==null)return;var d=new OpenFileDialog{Filter="视频|*.mp4;*.mov;*.mkv;*.avi"};if(d.ShowDialog(this)!=true)return;var info=await MediaTools.ProbeAsync(d.FileName);if(info.Width<1||info.Height<1||info.Duration<=0)throw new InvalidDataException("视频没有可用画面。");var relative="media/camera-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(d.FileName);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.Copy(d.FileName,path);auxiliaryDurations[relative]=info.Duration;ApplyEdit(p=>p.Camera=EffectDefaults.ApplyNewCameraDefaults(new(){MediaPath=relative,Duration=info.Duration,AssetAspectRatio=info.Width/(double)info.Height}),"添加摄像头");
 }
 private async Task ImportAudio()
 {
  if(projectFile==null)return;var d=new OpenFileDialog{Filter="音频/视频|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.mp4;*.mov"};if(d.ShowDialog(this)!=true)return;var info=await MediaTools.ProbeAsync(d.FileName);if(!info.HasAudio)throw new InvalidDataException("素材没有音轨。");var relative="media/audio-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(d.FileName);var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);File.Copy(d.FileName,path);auxiliaryDurations[relative]=info.Duration;ApplyEdit(p=>p.AudioTracks.Add(new(){MediaPath=relative,Duration=info.Duration}));
 }

}
