using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using FrameTrail.Capture;
namespace FrameTrail.App;
public partial class MainWindow
{
 private Window? recorderSetup;
 private bool changingRecorderMode;
 private void RegisterRecordingMarkerHotKey()
 {
  markerHotKey.Register(new System.Windows.Interop.WindowInteropHelper(this).Handle);
  if(markerHotKey.IsFallback)Status.Text="Ctrl+Alt+M 已被占用，录制标记使用 "+markerHotKey.ShortcutText+"。";
  else if(markerHotKey.ShortcutText==null)Status.Text="标记快捷键暂不可用，请使用录制条上的标记按钮。";
 }
 private string RecordingMarkerShortcutHelp=>markerHotKey.ShortcutText is { } shortcut
  ? shortcut+"　录制中添加标记"+(markerHotKey.IsFallback?"（Ctrl+Alt+M 已被占用，当前使用备用键）":"")
  : "录制标记快捷键暂不可用，请使用录制条上的标记按钮。";
 private async Task ShowFloatingRecorder()
 {
  if(recordingBar!=null){recordingBar.Activate();return;}
  if(recorderSelecting)return;
  if(recorderSetup!=null){recorderSetup.Show();recorderSetup.Activate();return;}await RefreshInputDevices();
  if(recorderSetup!=null){recorderSetup.Activate();return;}
  ApplySavedCaptureMode();ReconcileSavedDevices();
  var window=CreateFloatingRecorderWindow();
  recorderSetup=window;
  window.Closed+=(_,_)=>recorderSetup=null;
  FloatingWindowPlacement.Attach(window,"recorder");
  if(window.WindowStartupLocation==WindowStartupLocation.CenterScreen)CenterOnCursorDisplay(window);
  window.SourceInitialized+=(_,_)=>NativeWindows.Exclude(window);
  window.Show();
 }
 internal Window CreateFloatingRecorderWindow()=>BuildFloatingRecorderWindow();
}
