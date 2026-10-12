using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FrameTrail.Capture;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private readonly CaptureTrackService trackCapture=new();
 private readonly List<CaptureTrackHandle> recordingTracks=[];
 private readonly List<string> recordingTrackWarnings=[];
 private bool recordingPauseTransition;
 private async Task StartAdditionalTracks()
 {
  recordingTracks.Clear();recordingTrackWarnings.Clear();
  try
  {
   if(SystemSound.IsChecked==true)recordingTracks.Add(await trackCapture.StartAudioTrackAsync(new(Path.Combine(recordingFolder!,"media","system.wav"),AudioDeviceKind.SystemLoopback)));
   if(Microphone.IsChecked==true)recordingTracks.Add(await trackCapture.StartAudioTrackAsync(new(Path.Combine(recordingFolder!,"media","microphone.wav"),AudioDeviceKind.Microphone,(MicDevices.SelectedItem as AudioDeviceInfo)?.Id)));
   if(RecordCamera.IsChecked==true)
   {
    if(CameraDevices.SelectedItem is not CameraInfo camera)throw new InvalidOperationException("未找到可用摄像头，请连接设备或关闭摄像头录制。");
    var format=camera.Formats.Where(x=>x.Width<=1920&&x.FrameRate>=29).OrderByDescending(x=>x.Width*x.Height).FirstOrDefault()??camera.Formats.FirstOrDefault();
    recordingTracks.Add(await trackCapture.StartCameraAsync(new(Path.Combine(recordingFolder!,"media","camera.mp4"),camera.Id,format?.Index,new(FrameRate:30))));
   }
   await File.WriteAllTextAsync(Path.Combine(recordingFolder!,"independent-tracks.json"),JsonSerializer.Serialize(recordingTracks,new JsonSerializerOptions{WriteIndented=true}));
  }
  catch
  {
   // Only the independent tracks are stopped here. ToggleRecording rolls back the screen capture and the recording folder (D-01), so no "recording finished" is raised for this aborted start.
   await StopAdditionalTracks();throw;
  }
 }
 private async Task<List<CaptureTrackResult>> StopAdditionalTracks()
 {
  var output=new List<CaptureTrackResult>();foreach(var handle in recordingTracks.ToArray())try{output.Add(await trackCapture.StopTrackAsync(handle.TrackId));}catch(Exception ex){App.Log(ex);recordingTrackWarnings.Add(Path.GetFileName(handle.MediaPath)+" 未能正常结束："+ex.Message);if(!faultedTrackIds.Contains(handle.TrackId))ReportRecorderNotice("独立素材未能正常结束："+ex.Message,true);}recordingTracks.Clear();return output;
 }
 private Task ToggleRecordingPause()=>ToggleRecordingPauseCore(automatic:false);
 /// <summary>
 /// Pauses or resumes the screen capture and every independent track together. A track that already failed on its own (D-11) is left out.
 /// <paramref name="automatic"/> marks the pause done because the captured window was minimized (D-24), so restoring the window resumes it.
 /// </summary>
 private async Task ToggleRecordingPauseCore(bool automatic)
 {
  if(recordingFinalizing||recordingPauseTransition)return;recordingPauseTransition=true;
  try{
   var liveTracks=recordingTracks.Where(track=>!faultedTrackIds.Contains(track.TrackId)).ToArray();
   if(capture.State==CaptureState.Recording){await Task.WhenAll(liveTracks.Select(track=>trackCapture.PauseTrackAsync(track.TrackId)).Prepend(capture.PauseAsync()));recordingBar?.SetPaused(true);windowMinimizeAutoPaused=automatic;if(!automatic)ReportRecorderNotice("录制已暂停",false);}
   else if(capture.State==CaptureState.Paused){await Task.WhenAll(liveTracks.Select(track=>trackCapture.ResumeTrackAsync(track.TrackId)).Prepend(capture.ResumeAsync()));recordingBar?.SetPaused(false);windowMinimizeAutoPaused=false;ReportRecorderNotice(automatic?"窗口已恢复，继续录制":"继续录制",false);}
  }catch(Exception ex){App.Log(ex);try{await StopRecording();}catch(Exception stopError){App.Log(stopError);await StopAdditionalTracks();}throw new InvalidOperationException("某个轨道未能暂停或恢复，已结束本次录制并保留素材，避免继续生成不同步轨道。",ex);}finally{recordingPauseTransition=false;}
 }
 private static double MediaTime(long tick,RecordingResult result)
 {
  var anchor=result.FirstFrameObservedStopwatchTicks??result.StartStopwatchTicks;var elapsed=tick-anchor;foreach(var interval in result.PausedIntervals??[])elapsed-=Math.Max(0,Math.Min(tick,interval.ResumeStopwatchTicks)-Math.Max(anchor,interval.PauseStartStopwatchTicks));return elapsed/(double)result.StopwatchFrequency;
 }
 private async Task AttachRecordedTracks(FrameTrailProject created,RecordingResult screen,IReadOnlyList<CaptureTrackResult> tracks)
 {
  foreach(var track in tracks)
  {
   try{
    recordingTrackWarnings.AddRange(track.Warnings.Select(x=>Path.GetFileName(track.Handle.MediaPath)+": "+x));if(!File.Exists(track.Handle.MediaPath))throw new FileNotFoundException("独立素材文件未写入。",track.Handle.MediaPath);
    var info=await MediaTools.ProbeAsync(track.Handle.MediaPath);if(!double.IsFinite(info.Duration)||info.Duration<=0)throw new InvalidDataException("独立素材没有有效时长。");
    var relative=Path.GetRelativePath(recordingFolder!,track.Handle.MediaPath).Replace('\\','/');
    if(track.Handle.Kind==CaptureTrackKind.Camera){if(info.Width<1||info.Height<1)throw new InvalidDataException("摄像头素材没有视频轨。");AttachMappedCamera(created,screen,track,relative,info.Duration,info.Width/(double)info.Height);if(created.Camera==null)throw new InvalidDataException("摄像头与屏幕没有可对齐的有效时段。");}
    else{if(!info.HasAudio)throw new InvalidDataException("独立素材没有声音轨。");var before=created.AudioTracks.Count;var source=track.Handle.Kind==CaptureTrackKind.SystemAudio?AudioTrackSource.System:AudioTrackSource.Microphone;AttachMappedAudio(created,screen,track,relative,info.Duration,source);if(before==created.AudioTracks.Count)throw new InvalidDataException("声音与屏幕没有可对齐的有效时段。");}
   }catch(Exception ex){App.Log(ex);recordingTrackWarnings.Add(Path.GetFileName(track.Handle.MediaPath)+" 未加入工程，原始文件与时钟记录已保留："+ex.Message);}
  }
  await File.WriteAllTextAsync(Path.Combine(recordingFolder!,"independent-tracks-result.json"),JsonSerializer.Serialize(tracks,new JsonSerializerOptions{WriteIndented=true}));
  if(recordingTrackWarnings.Count>0)await File.WriteAllLinesAsync(Path.Combine(recordingFolder!,"recording-warnings.txt"),recordingTrackWarnings);
 }
 private static void AttachMappedAudio(FrameTrailProject created,RecordingResult screen,CaptureTrackResult track,string relative,double sourceDuration,AudioTrackSource source=AudioTrackSource.Other)
 {
  if(track.AudioSampleRate is int sampleRate&&sampleRate>0&&track.AudioClockSegments is {Count:>0} clockSegments)
  {
   if(track.Handle.StopwatchFrequency!=screen.StopwatchFrequency)throw new InvalidDataException("屏幕和独立音频的单调时钟频率不同，无法按实测分段时钟对齐。");
   if(clockSegments.Any(segment=>segment.SampleRate!=sampleRate||segment.SampleRate<=0||segment.SourceStartFrame<0||segment.SourceEndFrame<segment.SourceStartFrame||!double.IsFinite(segment.SourceRate)||segment.SourceRate<=0||(track.AudioFramesWritten is long frames&&segment.SourceEndFrame>frames)))
    throw new InvalidDataException("独立音频时钟分段的采样率、帧范围或SourceRate无效，无法安全对齐。");
   var screenPauses=(screen.PausedIntervals??[]).Select(p=>new ClockInterval(p.PauseStartStopwatchTicks,p.ResumeStopwatchTicks)).ToArray();
   var audioSegments=clockSegments.Select(segment=>new AudioClockSegmentTicks(segment.StartStopwatchTicks,segment.EndStopwatchTicks,
    segment.SourceStartFrame/(double)sampleRate,segment.SourceRate)).ToArray();
   var mapped=RecordingTimelineMapper.MapAudioClockSegments(screen.FirstFrameObservedStopwatchTicks??screen.StartStopwatchTicks,
    new(screen.StartStopwatchTicks,screen.StopStopwatchTicks),screenPauses,audioSegments,screen.StopwatchFrequency,sourceDuration,created.Media.Duration);
   if(mapped.Count==0)return;var first=mapped[0];var last=mapped[^1];var syncSegments=mapped.Select(x=>new CameraSyncSegment(x.SourceStart,x.TimelineStart,x.Duration,x.SourceRate)).ToList();
   created.AudioTracks.Add(new(){MediaPath=relative,SourceStart=first.SourceStart,Duration=last.TimelineStart+last.Duration-first.TimelineStart,OffsetSeconds=first.TimelineStart,SourceRate=first.SourceRate,SyncSegments=syncSegments,Source=source});return;
  }
  foreach(var interval in MapRecordedTrack(created,screen,track,sourceDuration))created.AudioTracks.Add(new(){MediaPath=relative,SourceStart=interval.SourceStart,Duration=interval.Duration,OffsetSeconds=interval.TimelineStart,Source=source});
 }
 private static void AttachMappedCamera(FrameTrailProject created,RecordingResult screen,CaptureTrackResult track,string relative,double sourceDuration,double assetAspectRatio)
 {
  var intervals=MapRecordedTrack(created,screen,track,sourceDuration);if(intervals.Count==0)return;
  var first=intervals[0];var end=intervals[^1].TimelineStart+intervals[^1].Duration;
  created.Camera=new(){MediaPath=relative,AssetAspectRatio=assetAspectRatio,SourceStart=first.SourceStart,TimelineStart=first.TimelineStart,Duration=end-first.TimelineStart,SyncSegments=intervals.Select(x=>new CameraSyncSegment(x.SourceStart,x.TimelineStart,x.Duration)).ToList()};
 }
 private static List<MappedAudioInterval> MapRecordedTrack(FrameTrailProject created,RecordingResult screen,CaptureTrackResult track,double sourceDuration)
 {
  if(track.Handle.StopwatchFrequency!=screen.StopwatchFrequency)throw new InvalidDataException("屏幕和音频的单调时钟频率不同，无法自动对齐。");
  var mapped=RecordingTimelineMapper.MapAudio(screen.FirstFrameObservedStopwatchTicks??screen.StartStopwatchTicks,new(screen.StartStopwatchTicks,screen.StopStopwatchTicks),(screen.PausedIntervals??[]).Select(p=>new ClockInterval(p.PauseStartStopwatchTicks,p.ResumeStopwatchTicks)).ToArray(),new(track.Handle.StartStopwatchTicks,track.StopStopwatchTicks),track.PausedIntervals.Select(p=>new ClockInterval(p.PauseStartStopwatchTicks,p.ResumeStopwatchTicks)).ToArray(),screen.StopwatchFrequency);
  var result=new List<MappedAudioInterval>();foreach(var interval in mapped){var duration=Math.Min(interval.Duration,Math.Min(sourceDuration-interval.SourceStart,created.Media.Duration-interval.TimelineStart));if(duration>.001)result.Add(interval with{Duration=duration});}return result;
 }
}
