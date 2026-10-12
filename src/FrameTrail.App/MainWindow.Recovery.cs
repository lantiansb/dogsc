using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using FrameTrail.Capture;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private async Task<string> RecoverReadableRecording(string folder)
 {
  var media=Path.Combine(folder,"media","screen.mp4");var notes=new List<string>();MediaInfo? info=null;Exception? mediaError=null;
  if(File.Exists(media))try{info=await MediaTools.ProbeAsync(media);}catch(Exception ex){mediaError=ex;}
  if(info==null||info.Duration<=0||info.Width==0){var candidates=FrameCaptureService.FindRecoverableRecordings(folder).Where(x=>Path.GetFileName(x.OriginalOutputPath).Equals("screen.mp4",StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>File.GetLastWriteTimeUtc(x.ManifestPath)).ToArray();if(candidates.Length==0)throw new InvalidDataException("录制中断前未留下可解码的视频或已封装片段。原始素材与日志已保留："+folder,mediaError);media=Path.Combine(folder,"media","screen-recovered-"+Guid.NewGuid().ToString("N")[..8]+".mp4");await FrameCaptureService.RecoverReadableRecordingAsync(candidates[0],media);info=await MediaTools.ProbeAsync(media);notes.Add("已从正常封装的录制片段恢复画面；中断时未封装的末段与原文件保留，未加入恢复工程。");}
  if(info.Duration<=0||info.Width==0)throw new InvalidDataException("素材没有完整可读的视频帧。");
  var recovered=MakeProject(Path.GetRelativePath(folder,media).Replace('\\','/'),info);RecordingResult? timing=null;
  try{var resultPath=Path.Combine(folder,"capture-result.json");if(File.Exists(resultPath))timing=JsonSerializer.Deserialize<RecordingResult>(await File.ReadAllTextAsync(resultPath));}catch(Exception ex){notes.Add("录制时钟记录无法读取："+ex.Message);}
  var handles=new List<CaptureTrackHandle>();try{var handlePath=Path.Combine(folder,"independent-tracks.json");if(File.Exists(handlePath))handles=JsonSerializer.Deserialize<List<CaptureTrackHandle>>(await File.ReadAllTextAsync(handlePath))??[];}catch(Exception ex){notes.Add("独立轨时钟记录无法读取："+ex.Message);}
  var results=new List<CaptureTrackResult>();try{var resultPath=Path.Combine(folder,"independent-tracks-result.json");if(File.Exists(resultPath))results=JsonSerializer.Deserialize<List<CaptureTrackResult>>(await File.ReadAllTextAsync(resultPath))??[];}catch(Exception ex){notes.Add("独立轨暂停记录无法读取："+ex.Message);}
  foreach(var name in new[]{"system.wav","microphone.wav","camera.mp4"})
  {
   var relative="media/"+name;var asset=Path.Combine(folder,"media",name);if(!File.Exists(asset))continue;
   try{var trackInfo=await MediaTools.ProbeAsync(asset);if(trackInfo.Duration<=0)continue;var handle=handles.FirstOrDefault(x=>Path.GetFileName(x.MediaPath).Equals(name,StringComparison.OrdinalIgnoreCase));var trackResult=results.FirstOrDefault(x=>Path.GetFileName(x.Handle.MediaPath).Equals(name,StringComparison.OrdinalIgnoreCase));var offset=timing!=null&&handle!=null?MediaTime(handle.StartStopwatchTicks,timing):0;if(timing==null||handle==null)notes.Add(name+" 缺少完整首帧时钟，已按零偏移恢复，可在声音/摄像头面板校准。");if(name=="camera.mp4"&&trackInfo.Width>0&&trackInfo.Height>0){var aspectRatio=trackInfo.Width/(double)trackInfo.Height;if(timing!=null&&trackResult!=null)AttachMappedCamera(recovered,timing,trackResult,relative,trackInfo.Duration,aspectRatio);else{recovered.Camera=EffectDefaults.ApplyNewCameraDefaults(new(){MediaPath=relative,Duration=trackInfo.Duration,TimelineStart=offset,AssetAspectRatio=aspectRatio});notes.Add("camera.mp4 缺少完整相机暂停记录，已按连续素材恢复；暂停后的局部同步需要校准。");}}else if(trackInfo.HasAudio){var source=trackResult?.Handle.Kind==CaptureTrackKind.SystemAudio||name.Equals("system.wav",StringComparison.OrdinalIgnoreCase)?AudioTrackSource.System:AudioTrackSource.Microphone;if(timing!=null&&trackResult!=null)AttachMappedAudio(recovered,timing,trackResult,relative,trackInfo.Duration,source);else{recovered.AudioTracks.Add(new(){MediaPath=relative,Duration=trackInfo.Duration,OffsetSeconds=offset,Source=source});if(timing?.PausedIntervals?.Count>0)notes.Add(name+" 缺少音轨暂停记录，已恢复原始连续音轨；多次暂停后的局部同步需校准。");}}}
   catch(Exception ex){notes.Add(name+" 未加入工程，原文件保留："+ex.Message);}
  }
  var pointerPath=Path.Combine(folder,"pointer.jsonl");if(timing!=null&&File.Exists(pointerPath))foreach(var line in File.ReadLines(pointerPath))try{var input=JsonSerializer.Deserialize<InputEvent>(line);if(input==null||input.Kind is not (InputEventKind.Move or InputEventKind.MouseDown))continue;if(timing.PausedIntervals?.Any(x=>input.StopwatchTicks>=x.PauseStartStopwatchTicks&&input.StopwatchTicks<x.ResumeStopwatchTicks)==true)continue;var time=MediaTime(input.StopwatchTicks,timing);if(time<0||time>info.Duration)continue;var kind=input.Kind==InputEventKind.Move?PointerEventKind.Move:input.MouseButton==FrameTrail.Capture.MouseButton.Right?PointerEventKind.RightClick:PointerEventKind.LeftClick;recovered.PointerEvents.Add(new(time,new(input.NormalizedX,input.NormalizedY),kind,input.CursorAsset));}catch(JsonException){notes.Add("忽略一条未写完的指针日志。");}
  else if(File.Exists(pointerPath))notes.Add("没有首帧时钟，指针原始日志保留，未自动加入工程。");
  var markersPath=Path.Combine(folder,"markers.jsonl");if(timing!=null&&File.Exists(markersPath))foreach(var line in File.ReadLines(markersPath))try{using var json=JsonDocument.Parse(line);if(!json.RootElement.TryGetProperty("Tick",out var tickValue)||!tickValue.TryGetInt64(out var tick)){notes.Add("忽略缺少时间的标记记录。");continue;}var label=json.RootElement.TryGetProperty("Label",out var labelValue)&&labelValue.ValueKind==JsonValueKind.String?labelValue.GetString()??"标记":"标记";recovered.Markers.Add(new(Guid.NewGuid(),Math.Clamp(MediaTime(tick,timing),0,info.Duration),label));}catch(JsonException){notes.Add("忽略一条未写完的标记日志。");}
  recovered.ZoomClips=AutoZoomPlanner.Plan(recovered.PointerEvents);var path=Path.Combine(folder,"recovered-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".frametrail");FrameTrailProjectStore.Save(path,recovered);await File.WriteAllLinesAsync(path+".recovery.txt",notes.Count>0?notes:["已恢复可读视频、独立轨道与可对齐的指针记录。"]);return path;
 }
 // ---- finding and presenting interrupted recordings (Mac: RecorderPanelController "恢复中断录制") ---------------------------

 /// <summary>
 /// Recording folders under <paramref name="root"/> that still carry a recovery journal and some media, newest first.
 /// The folder being recorded right now (<paramref name="activeFolder"/>) is never listed.
 /// </summary>
 internal static IReadOnlyList<string> ScanRecoverableFolders(string root,string? activeFolder)
 {
  var found=new List<(string Folder,DateTime Time)>();
  try
  {
   if(!Directory.Exists(root))return [];
   var active=activeFolder is null?null:Path.GetFullPath(activeFolder).TrimEnd('\\','/');
   foreach(var folder in Directory.EnumerateDirectories(root))
   {
    try
    {
     var journal=Path.Combine(folder,"recording-recovery.json");
     if(!File.Exists(journal))continue;
     if(active!=null&&Path.GetFullPath(folder).TrimEnd('\\','/').Equals(active,StringComparison.OrdinalIgnoreCase))continue;
     var media=Path.Combine(folder,"media");
     // A journal without any written media has nothing to recover (it is a start that failed before the first frame).
     if(!Directory.Exists(media)||!Directory.EnumerateFileSystemEntries(media).Any())continue;
     found.Add((folder,File.GetLastWriteTimeUtc(journal)));
    }
    catch(Exception error){App.Log(error);}
   }
  }
  catch(Exception error){App.Log(error);}
  return found.OrderByDescending(x=>x.Time).Select(x=>x.Folder).ToArray();
 }

 /// <summary>Interrupted recording folders in the user's recording directory, newest first (for the tray / configuration menu).</summary>
 internal IReadOnlyList<string> FindRecoverableRecordings()
 {
  var busy=capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping||recordingStarting||recordingFinalizing;
  return ScanRecoverableFolders(RecordingRootDirectory,busy?recordingFolder:null);
 }

 /// <summary>One-click recovery of the newest interrupted recording; the result opens on the same completion card as a new recording.</summary>
 internal async Task RecoverLatestRecordingAsync()
 {
  if(capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping||recordingStarting||recordingFinalizing)
   throw new InvalidOperationException("正在录制，请结束后再恢复中断的录制。");
  var latest=FindRecoverableRecordings().FirstOrDefault();
  if(latest is null)throw new InvalidOperationException("没有找到可恢复的中断录制。");
  await RecoverAndPresentAsync(latest);
 }

 /// <summary>Recovers <paramref name="folder"/>, marks its journal as handled and shows the result card.</summary>
 private async Task RecoverAndPresentAsync(string folder)
 {
  if(capture.State is CaptureState.Starting or CaptureState.Recording or CaptureState.Paused or CaptureState.Stopping||recordingStarting||recordingFinalizing)
   throw new InvalidOperationException("正在录制，请结束后再恢复中断的录制。");
  if(!File.Exists(Path.Combine(folder,"recording-recovery.json")))throw new FileNotFoundException("所选目录里没有 recording-recovery.json。",folder);
  Status.Text="正在恢复中断的录制…";
  var path=await RecoverReadableRecording(folder);
  // The journal is renamed, not deleted, so the folder stops being offered but nothing the user may still need is lost.
  try
  {
   var journal=Path.Combine(folder,"recording-recovery.json");var handled=journal+".recovered";
   if(File.Exists(handled))File.Delete(handled);
   File.Move(journal,handled);
  }
  catch(Exception error){App.Log(error);}
  var opened=FrameTrailProjectStore.Load(path);
  var width=Math.Max(2,(int)opened.Media.Width);var height=Math.Max(2,(int)opened.Media.Height);
  BitmapSource? thumbnail=null;
  try{thumbnail=await DecodeCompletionThumbnailAsync(Path.Combine(folder,opened.MediaPath.Replace('/',Path.DirectorySeparatorChar)),width,height);}
  catch(Exception error){App.Log(error);}
  var title=string.IsNullOrWhiteSpace(opened.DisplayName)?"恢复的录制":opened.DisplayName!.Trim();
  var duration=opened.Media.Duration;
  Status.Text="已恢复中断的录制：详情见 "+Path.GetFileName(path)+".recovery.txt";
  CloseRecordingBar();
  ShowCompletionCard(new RecordingCompletionInfo(path,folder,title,duration,width,height,thumbnail,"这是从中断的录制中恢复出的内容，末尾可能缺失。"),CompletionWorkArea());
 }

 /// <summary>Once per launch: if an earlier recording was interrupted, offer to bring it back (the tray menu keeps the entry afterwards).</summary>
 private async Task OfferRecoveryAtStartupAsync()
 {
  if(recoveryOffered)return;
  recoveryOffered=true;
  var folders=await Task.Run(FindRecoverableRecordings);
  if(folders.Count==0||desktopExiting||closeReady)return;
  if(capture.State is not CaptureState.Idle||recordingStarting||completionWindow!=null)return;
  DateTime latest;
  try{latest=File.GetLastWriteTime(Path.Combine(folders[0],"recording-recovery.json"));}
  catch{latest=DateTime.Now;}
  recoveryOffer?.Close();
  var offer=new RecoveryOfferWindow(folders.Count,latest,CompletionWorkArea(),RecoverLatestRecordingAsync);
  recoveryOffer=offer;
  offer.Closed+=(_,_)=>{if(ReferenceEquals(recoveryOffer,offer))recoveryOffer=null;};
  offer.Show();
 }
}
