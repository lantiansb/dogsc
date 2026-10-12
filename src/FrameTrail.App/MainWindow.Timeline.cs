using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace FrameTrail.App;
public partial class MainWindow
{
 private string? effectClipboardProjectFile;
 /// <summary>Commits a finished timeline drag as one labelled undo step, then places the playhead like Mac does after a primary edit.</summary>
 private void ApplyTimelineChange(TimelineChange c)
 {
  var before=project;
  ApplyEdit(p=>ApplyChange(p,c),ChangeLabel(c));
  if(project==null||ReferenceEquals(before,project))return;
  if(c.Selection.Kind=="screen"&&c.Operation is "trim-start" or "trim-end" or "retime")
  {
   var elapsed=0d;
   foreach(var s in project.Timeline.Segments)
   {
    if(s.Id==c.Selection.Id){SeekToOutputTime(c.Operation=="trim-start"?elapsed:elapsed+s.OutputDuration);break;}
    elapsed+=s.OutputDuration;
   }
  }
 }
 private static string ChangeLabel(TimelineChange c)
 {
  if(c.Selection.Kind=="screen")return c.Operation switch{"reorder"=>"重排片段","retime"=>"调整片段速度",_=>"裁剪片段"};
  var name=c.Selection.Kind switch{"camera"=>"摄像头","audio"=>"音轨",var k=>EffectLaneSupport.KindName(k)};
  return c.Operation=="move"?"移动"+name:"调整"+name+"长度";
 }
 /// <summary>Shows what a running drag would commit in the canvas without touching history (effects only).</summary>
 private void PreviewTimelineChange(TimelineChange c)
 {
  if(c.Selection.Kind is "camera" or "audio")return;
  if(c.Selection.Kind=="screen"){if(c.Operation is "trim-start" or "trim-end")PreviewTrimScrub(c);return;}
  PreviewLiveEdit(p=>ApplyChange(p,c));
 }
 /// <summary>Applies the change and rejects results that would overlap clips on an exclusive lane (zoom, 3D, camera motion).</summary>
 private void ApplyChange(FrameTrailProject p,TimelineChange c)
 {
  var many=TimelineView.MultiSelection.ToArray();
  ApplyChangeCore(p,c,many);
  foreach(var s in many.Append(c.Selection).Distinct())EnsureExclusiveLane(p,s.Kind,s.Id);
 }
 private static void EnsureExclusiveLane(FrameTrailProject p,string kind,Guid id)
 {
  if(!EffectLaneSupport.IsExclusive(kind))return;
  var lane=EffectLaneSupport.Items(p,kind);var me=lane.FirstOrDefault(x=>x.Id==id);if(me.Id!=id)return;
  if(lane.Any(o=>o.Id!=id&&EffectLaneSupport.Overlaps(me.Start,me.End,o.Start,o.End)))throw new InvalidOperationException("此处已有同轨道动画，片段不能重叠。");
 }
 private void ApplyChangeCore(FrameTrailProject p,TimelineChange c,TimelineSelection[] many)
 {
  if(c.Operation=="reorder"&&c.Selection.Kind=="screen"){
   var selectedIds=many.Where(s=>s.Kind=="screen").Select(s=>s.Id).ToHashSet();
   if(selectedIds.Count>1&&selectedIds.Contains(c.Selection.Id)){var original=p.Timeline.Segments;var group=original.Where(s=>selectedIds.Contains(s.Id)).ToList();var first=original.FindIndex(s=>selectedIds.Contains(s.Id));var anchor=original.FindIndex(s=>s.Id==c.Selection.Id);var remaining=original.Where(s=>!selectedIds.Contains(s.Id)).ToList();var index=Math.Clamp(c.NewIndex,0,remaining.Count);remaining.InsertRange(index,group);p.Timeline.Segments=remaining;return;}
  }
  if(c.Operation=="move"&&many.Length>1&&many.Contains(c.Selection)&&Span(p,c.Selection) is {} span){var group=many.Select(s=>(Selection:s,Span:Span(p,s))).Where(x=>x.Span.HasValue).Select(x=>(x.Selection,Span:x.Span!.Value)).ToArray();var lower=-group.Min(x=>x.Span.Start);var upper=group.Min(x=>p.Media.Duration-x.Span.Start-x.Span.Duration);var delta=Math.Clamp(c.SourceStart-span.Start,lower,Math.Max(lower,upper));foreach(var item in group)ApplyOneTimelineChange(p,new(item.Selection,"move",item.Span.Start+delta,item.Span.Duration));return;}
  ApplyOneTimelineChange(p,c);
 }
 private static (double Start,double Duration)? Span(FrameTrailProject p,TimelineSelection s)=>s.Kind switch
 {"camera"=>p.Camera is {} camera?(camera.TimelineStart,camera.Duration):null,"audio"=>Enumerable.Range(0,p.AudioTracks.Count).Where(i=>EditorTimelineControl.AudioId(i)==s.Id).Select(i=>((double,double)?)(p.AudioTracks[i].OffsetSeconds,p.AudioTracks[i].Duration)).FirstOrDefault(),"zoom"=>p.ZoomClips.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.End-x.Start)).FirstOrDefault(),"motion"=>p.ScreenMotions.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),"cameraMotion"=>p.CameraMotions.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),"overlay"=>p.Overlays.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),"blur"=>p.BlurClips.Where(x=>x.Id==s.Id).Select(x=>((double,double)?)(x.Start,x.Duration)).FirstOrDefault(),_=>null};
 private void ApplyOneTimelineChange(FrameTrailProject p,TimelineChange c)
 {
  if(c.Selection.Kind=="screen")
  {
   var s=p.Timeline.Segments.First(x=>x.Id==c.Selection.Id);
   if(c.Operation=="reorder")p.Timeline.Reorder(s.Id,Math.Clamp(c.NewIndex,0,p.Timeline.Segments.Count-1));
   else if(c.Operation=="retime")p.Timeline.SetPlaybackRate(s.Id,Math.Clamp(c.Rate,.25,100));
   else
   {
    // A trim may only reveal source that no other segment uses (Mac trimDraftOrigin); shrinking is always fine.
    var newEnd=c.SourceStart+c.SourceDuration;
    var shrinks=c.SourceStart>=s.SourceStart-1e-6&&newEnd<=s.SourceEnd+1e-6;
    if(!shrinks&&p.Timeline.Segments.Any(o=>o.Id!=s.Id&&o.SourceStart<newEnd-1e-6&&o.SourceEnd>c.SourceStart+1e-6))throw new InvalidOperationException("裁剪范围会与相邻片段的素材重复，已取消。");
    s.SourceStart=c.SourceStart;s.SourceDuration=c.SourceDuration;
   }
  }
  else if(c.Selection.Kind=="zoom"){var z=p.ZoomClips.First(x=>x.Id==c.Selection.Id);var startChanged=Math.Abs(z.Start-c.SourceStart)>1e-6;var endChanged=Math.Abs(z.End-(c.SourceStart+c.SourceDuration))>1e-6;z.Start=c.SourceStart;z.End=c.SourceStart+c.SourceDuration;if(c.Tail>=0)z.ExitDuration=Math.Min(z.ExitDuration,c.Tail);if(c.Operation=="move"&&startChanged){z.EnterProgressOffset=0;z.ExitProgressOffset=0;}else{if(c.Operation=="trim-start"&&startChanged)z.EnterProgressOffset=0;if(c.Operation=="trim-end"&&endChanged)z.ExitProgressOffset=0;}}
  else if(c.Selection.Kind=="motion"){var z=p.ScreenMotions.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;if(c.Tail>=0)z.ReturnDuration=Math.Min(z.ReturnDuration,c.Tail);if(c.Operation is "trim-start" or "trim-end")z.ReturnDuration=Math.Min(z.ReturnDuration,z.Duration);}
  else if(c.Selection.Kind=="cameraMotion"){var z=p.CameraMotions.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;if(c.Tail>=0)z.ReturnDuration=Math.Min(z.ReturnDuration,c.Tail);if(c.Operation is "trim-start" or "trim-end")z.ReturnDuration=Math.Min(z.ReturnDuration,z.Duration);}
  else if(c.Selection.Kind=="overlay"){var z=p.Overlays.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;}
  else if(c.Selection.Kind=="blur"){var z=p.BlurClips.First(x=>x.Id==c.Selection.Id);z.Start=c.SourceStart;z.Duration=c.SourceDuration;}
  else if(c.Selection.Kind=="camera"&&p.Camera!=null){var a=p.Camera;if(a.SyncSegments is {Count:>0}){if(c.Operation=="trim-start")CameraTimelineOperations.TrimTimelineStart(a,Math.Clamp(c.SourceStart,a.TimelineStart,a.TimelineStart+a.Duration-.04));else if(c.Operation=="trim-end")CameraTimelineOperations.TrimTimelineEnd(a,a.TimelineStart+Math.Clamp(c.SourceDuration,.04,a.Duration));else CameraTimelineOperations.Move(a,c.SourceStart-a.TimelineStart);return;}var available=AvailableAssetDuration(a.MediaPath,a.SourceStart+a.Duration);if(c.Operation=="trim-start"){var delta=Math.Clamp(c.SourceStart-a.TimelineStart,-a.SourceStart,a.Duration-.04);a.SourceStart+=delta;a.TimelineStart+=delta;a.Duration-=delta;}else{a.TimelineStart=c.SourceStart;a.Duration=Math.Min(c.SourceDuration,available-a.SourceStart);}}
  else if(c.Selection.Kind=="audio"){var index=Enumerable.Range(0,p.AudioTracks.Count).FirstOrDefault(i=>EditorTimelineControl.AudioId(i)==c.Selection.Id,-1);if(index>=0){var a=p.AudioTracks[index];var available=AvailableAssetDuration(a.MediaPath,a.SourceStart+a.Duration*a.SourceRate);if(a.SyncSegments is {Count:>0}){if(c.Operation=="trim-start")AudioTimelineOperations.TrimTimelineStart(a,Math.Clamp(c.SourceStart,a.OffsetSeconds,a.OffsetSeconds+a.Duration-.04));else if(c.Operation=="trim-end")AudioTimelineOperations.TrimTimelineEnd(a,Math.Clamp(c.SourceStart+c.SourceDuration,a.OffsetSeconds+.04,a.OffsetSeconds+a.Duration));else AudioTimelineOperations.Move(a,Math.Max(c.SourceStart-a.OffsetSeconds,-a.OffsetSeconds));}else if(c.Operation=="trim-start"){var delta=Math.Clamp(c.SourceStart-a.OffsetSeconds,-a.SourceStart/a.SourceRate,a.Duration-.04);a.SourceStart+=delta*a.SourceRate;a.OffsetSeconds+=delta;a.Duration-=delta;}else if(c.Operation=="trim-end"){a.Duration=Math.Min(a.Duration,Math.Max(.04,c.SourceStart+c.SourceDuration-a.OffsetSeconds));AudioTimelineOperations.TrimAssetEnd(a,available);}else{a.OffsetSeconds=c.SourceStart;a.Duration=Math.Min(c.SourceDuration,Math.Max(0,(available-a.SourceStart)/a.SourceRate));}}}
 }
 private void DeleteSelectionClick(object sender,RoutedEventArgs e)=>DeleteTimelineSelection();
 /// <summary>Deletes the explicit timeline selection only; with nothing selected nothing happens (Mac EditorTimelineDeleteTarget).</summary>
 private void DeleteTimelineSelection()
 {
  if(project==null)return;
  var targets=TimelineView.MultiSelection.Count>1?TimelineView.MultiSelection.ToArray():CurrentTimelineSelection() is {} one?new[]{one}:Array.Empty<TimelineSelection>();
  if(targets.Length==0)return;
  var before=project;
  ApplyEdit(p=>
  {
   var screens=targets.Where(s=>s.Kind=="screen").Select(s=>s.Id).ToHashSet();
   if(screens.Count>0&&p.Timeline.Segments.All(s=>screens.Contains(s.Id)))throw new InvalidOperationException(targets.Length>1?"至少保留一个屏幕片段。":"至少保留一个屏幕片段；可重新导入或建立新项目。");
   var audioIds=targets.Where(s=>s.Kind=="audio").Select(s=>s.Id).ToHashSet();
   var audioIndexes=Enumerable.Range(0,p.AudioTracks.Count).Where(i=>audioIds.Contains(EditorTimelineControl.AudioId(i))).OrderByDescending(i=>i).ToArray();
   if(screens.Count>0)RecordingMarkerRules.MoveMarkersToCutPoints(p,screens);
   foreach(var target in targets.Where(s=>s.Kind!="audio"))DeleteSelected(p,target);
   foreach(var index in audioIndexes)p.AudioTracks.RemoveAt(index);
  },targets.Length>1?"删除所选":"删除"+(targets[0].Kind=="screen"?"片段":targets[0].Kind=="camera"?"摄像头":targets[0].Kind=="audio"?"音轨":EffectLaneSupport.KindName(targets[0].Kind)));
  if(!ReferenceEquals(before,project))ClearTimelineSelectionState();
 }
 private static void DeleteSelected(FrameTrailProject p,TimelineSelection s)
 {
  switch(s.Kind){case "screen":p.Timeline.Segments.RemoveAll(x=>x.Id==s.Id);break;case "zoom":p.ZoomClips.RemoveAll(x=>x.Id==s.Id);break;case "motion":p.ScreenMotions.RemoveAll(x=>x.Id==s.Id);break;case "cameraMotion":p.CameraMotions.RemoveAll(x=>x.Id==s.Id);break;case "overlay":p.Overlays.RemoveAll(x=>x.Id==s.Id);break;case "blur":p.BlurClips.RemoveAll(x=>x.Id==s.Id);break;case "camera":p.Camera=null;break;case "audio":var i=Enumerable.Range(0,p.AudioTracks.Count).FirstOrDefault(i=>EditorTimelineControl.AudioId(i)==s.Id,-1);if(i>=0)p.AudioTracks.RemoveAt(i);break;}
 }
 /// <summary>Right-click menu built from what was hit (Mac per-lane context menus); a background click offers paste at that time.</summary>
 private void TimelineContextMenu(TimelineSelection? selection,double time)
 {
  if(project==null)return;
  var menu=new ContextMenu();
  MenuItem Item(string label,Action action,bool enabled=true){var m=new MenuItem{Header=label,IsEnabled=enabled};m.Click+=(_,_)=>action();menu.Items.Add(m);return m;}
  void Divide(){if(menu.Items.Count>0&&menu.Items[^1] is not Separator)menu.Items.Add(new Separator());}
  var timeline=project.Timeline;
  var isTimedEffect=selection is not null&&ToTimelineEffectKind(selection.Kind) is not null;
  var clipboardReady=effectClipboardJson!=null&&effectClipboardKind!=null;
  if(selection is {Kind:"screen"})
  {
   var id=selection.Id;var index=timeline.Segments.FindIndex(x=>x.Id==id);
   Item("在播放头分割  S",()=>SplitClick(this,new RoutedEventArgs()));
   Item("波纹删除播放头之前  Q",()=>TrimLeftClick(this,new RoutedEventArgs()));
   Item("波纹删除播放头之后  W",()=>TrimRightClick(this,new RoutedEventArgs()));
   Divide();
   if(index>0)SeamItems(index-1,"上一段");
   if(index>=0&&index<timeline.Segments.Count-1)SeamItems(index,"下一段");
   if(index==0&&TimelineSeamOperations.CanRestoreLeading(timeline))Item("还原开头剪辑",()=>OnTimelineSeamRequested("leading",0));
   if(index==timeline.Segments.Count-1&&TimelineSeamOperations.CanRestoreTrailing(timeline,project.Media.Duration))Item("还原结尾剪辑",()=>OnTimelineSeamRequested("trailing",index));
   void SeamItems(int seam,string side)
   {
    switch(TimelineSeamOperations.Classify(timeline,seam))
    {
     case TimelineSeamKind.Merge:Item($"与{side}合并",()=>OnTimelineSeamRequested("merge",seam));break;
     case TimelineSeamKind.Restore:Item($"还原此处剪切（{side}）",()=>OnTimelineSeamRequested("restore",seam));break;
    }
   }
   Divide();
   Item("向左重排",()=>ApplyEdit(p=>p.Timeline.Reorder(id,Math.Max(0,p.Timeline.Segments.FindIndex(x=>x.Id==id)-1)),"重排片段"),index>0);
   Item("向右重排",()=>ApplyEdit(p=>p.Timeline.Reorder(id,Math.Min(p.Timeline.Segments.Count-1,p.Timeline.Segments.FindIndex(x=>x.Id==id)+1)),"重排片段"),index>=0&&index<timeline.Segments.Count-1);
   var targets=TimelineView.MultiSelection.Where(x=>x.Kind=="screen").Select(x=>x.Id).ToHashSet();if(!targets.Contains(id)||targets.Count==0)targets=[id];
   var speed=new MenuItem{Header="片段速度"};var current=timeline.Segments.FirstOrDefault(x=>x.Id==id);
   foreach(var rate in new[]{.25,.5,.75,1d,1.5,2,4,8,12,20,50,100})
   {
    var option=new MenuItem{Header=$"{rate}×",IsCheckable=true,IsChecked=current is not null&&Math.Abs(current.PlaybackRate-rate)<1e-9};
    option.Click+=(_,_)=>ApplyEdit(p=>{foreach(var segmentId in targets)p.Timeline.SetPlaybackRate(segmentId,rate);},"调整片段速度");
    speed.Items.Add(option);
   }
   menu.Items.Add(speed);
   Item("删除所选  D / Delete",DeleteCurrentSelection);
  }
  else if(selection is not null)
  {
   if(isTimedEffect)
   {
    Item("在播放头分割  S",()=>SplitClick(this,new RoutedEventArgs()));
    Item("裁去起点  Q",()=>TrimLeftClick(this,new RoutedEventArgs()));
    Item("裁去终点  W",()=>TrimRightClick(this,new RoutedEventArgs()));
    Divide();
   }
   if(selection.Kind!="audio")Item("复制  Ctrl+C",CopyEffect);
   if(clipboardReady)Item(selection.Kind=="camera"?"粘贴并替换摄像头轨  Ctrl+V":"粘贴片段  Ctrl+V",()=>PasteEffect(false,time));
   if(clipboardReady)Item("粘贴属性  Ctrl+Shift+V",()=>PasteEffect(true,time),effectClipboardKind==selection.Kind);
   Divide();
   Item("删除所选  D / Delete",DeleteCurrentSelection);
  }
  else
  {
   Item("粘贴片段  Ctrl+V",()=>PasteEffect(false,time),clipboardReady);
  }
  Divide();
  Item("设置导出起点  I",()=>SetExportRange(true));
  Item("设置导出终点  O",()=>SetExportRange(false));
  Item("清除导出范围",()=>{TimelineView.RangeStart=null;TimelineView.RangeEnd=null;TimelineView.InvalidateVisual();});
  if(selection is null or {Kind:"screen"}){Divide();Item("全部恢复为原始录制…",ResetTimelineToOriginal);}
  menu.PlacementTarget=TimelineView;menu.IsOpen=true;
 }
 private void CopyEffect()
 {
  if(project==null||selectedEffect==null)return;object? value=selectedEffect.Kind switch{"zoom"=>project.ZoomClips.FirstOrDefault(x=>x.Id==selectedEffect.Id),"motion"=>project.ScreenMotions.FirstOrDefault(x=>x.Id==selectedEffect.Id),"cameraMotion"=>project.CameraMotions.FirstOrDefault(x=>x.Id==selectedEffect.Id),"camera"=>project.Camera,"overlay"=>project.Overlays.FirstOrDefault(x=>x.Id==selectedEffect.Id),"blur"=>project.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect.Id),_=>null};if(value==null)return;
  effectClipboardKind=selectedEffect.Kind;effectClipboardProjectFile=projectFile;effectClipboardJson=JsonSerializer.Serialize(value,value.GetType());Status.Text="已复制完整效果片段。";
 }
 /// <summary>Paste at the pointer's timeline position (or the playhead). Never shortens the copy and never overlaps an exclusive lane (Mac EditorEffectTransfer).</summary>
 private void PasteEffect(bool attributes,double? atOutputTime=null)
 {
  if(effectClipboardJson==null||effectClipboardKind==null||project==null)return;
  var kind=effectClipboardKind;var json=effectClipboardJson;
  _=Guard(()=>{PasteEffectCore(kind,json,attributes,atOutputTime);return Task.CompletedTask;});
 }
 private static double NextStartAfter(FrameTrailProject p,string kind,double end,Guid? ignore)
 {
  var items=EffectLaneSupport.Items(p,kind,ignore is {} id?new[]{id}:null);
  return items.Where(x=>x.Start>=end-1e-4).Select(x=>x.Start).DefaultIfEmpty(EffectLaneSupport.Limit(p)).Min();
 }
 private void PasteEffectCore(string kind,string json,bool attributes,double? atOutputTime)
 {
  var p0=project!;
  if(attributes&&selectedEffect?.Kind!=kind)throw new InvalidOperationException("请先选择同类效果片段。");
  T Read<T>()=>JsonSerializer.Deserialize<T>(json)!;
  var outputTime=atOutputTime??(TimelineView.IsMouseOver&&TimelineView.HoverTime is {} hover?hover:Playhead.Value);
  outputTime=Math.Clamp(outputTime,0,p0.Timeline.Duration);
  var limit=EffectLaneSupport.Limit(p0);var exclusive=EffectLaneSupport.IsExclusive(kind);
  var newId=Guid.NewGuid();double start=0,length=0;
  if(kind!="camera"&&!attributes)
  {
   length=kind switch{"zoom"=>Read<ZoomClip>().End-Read<ZoomClip>().Start,"motion"=>Read<ScreenMotionClip>().Duration,"cameraMotion"=>Read<CameraMotionClip>().Duration,"overlay"=>Read<OverlayClip>().Duration,_=>Read<BlurClip>().Duration};
   var lane=EffectLaneSupport.Items(p0,kind);
   start=p0.Timeline.MapOutputToSource(outputTime);
   var tolerance=TimelineView.Snap?7/Math.Max(TimelineView.Scale,1e-6):0;
   if(tolerance>0)start=EffectLaneRules.SnapPasteStart(lane,start,length,limit,tolerance,exclusive);
   start=Math.Max(0,start);
   if(start+length>limit+1e-6)throw new InvalidOperationException("此位置到片尾的时长不足，无法粘贴完整的片段。");
   if(exclusive&&!EffectLaneRules.HasSpace(lane,start,length))throw new InvalidOperationException("此处已有同轨道动画，请换一个位置粘贴。");
  }
  var pasted=attributes&&kind!="camera"?selectedEffect!.Id:kind=="camera"?Guid.Empty:newId;
  TimelineView.HiddenTracks.Remove(kind);SaveTimelineViewSoon();
  Edit(p=>
  {
   if(kind=="zoom")
   {
    var value=Read<ZoomClip>();if(value.Automatic)value.Focus=ToCropLocalFocus(p,value.Focus);
    var target=attributes?p.ZoomClips.First(x=>x.Id==selectedEffect!.Id):null;
    var s=target?.Start??start;var e=target?.End??start+length;var duration=e-s;
    value=value with{Id=pasted,Start=s,End=e,EnterProgressOffset=attributes?0:value.EnterProgressOffset,ExitProgressOffset=attributes?0:value.ExitProgressOffset,Automatic=false,EnterDuration=Math.Min(value.EnterDuration,duration),ExitDuration=EffectLaneRules.FitTailBeforeSuccessor(e,Math.Min(value.ExitDuration,duration),NextStartAfter(p,kind,e,target?.Id))};
    if(target!=null)p.ZoomClips.Remove(target);p.ZoomClips.Add(value);
   }
   else if(kind=="motion")
   {
    var value=Read<ScreenMotionClip>();var target=attributes?p.ScreenMotions.First(x=>x.Id==selectedEffect!.Id):null;
    var s=target?.Start??start;var duration=target?.Duration??length;
    value=value with{Id=pasted,Start=s,Duration=duration,EnterDuration=Math.Min(value.EnterDuration,duration),ReturnDuration=EffectLaneRules.FitTailBeforeSuccessor(s+duration,Math.Min(value.ReturnDuration,duration),NextStartAfter(p,kind,s+duration,target?.Id))};
    if(target!=null)p.ScreenMotions.Remove(target);p.ScreenMotions.Add(value);
   }
   else if(kind=="cameraMotion")
   {
    var value=Read<CameraMotionClip>();var target=attributes?p.CameraMotions.First(x=>x.Id==selectedEffect!.Id):null;
    var s=target?.Start??start;var duration=target?.Duration??length;
    value=value with{Id=pasted,Start=s,Duration=duration,EnterDuration=Math.Min(value.EnterDuration,duration),ReturnDuration=EffectLaneRules.FitTailBeforeSuccessor(s+duration,Math.Min(value.ReturnDuration,duration),NextStartAfter(p,kind,s+duration,target?.Id))};
    if(target!=null)p.CameraMotions.Remove(target);p.CameraMotions.Add(value);
   }
   else if(kind=="camera")
   {
    var value=Read<CameraTrack>();var target=attributes?(p.Camera??throw new InvalidOperationException("请先选择摄像头轨道。")):null;
    if(target!=null)value=value with{MediaPath=target.MediaPath,AssetAspectRatio=target.AssetAspectRatio,SourceStart=target.SourceStart,Duration=target.Duration,TimelineStart=target.TimelineStart,SyncSegments=target.SyncSegments?.Select(x=>x with{}).ToList()};
    else if(effectClipboardProjectFile!=null&&!string.Equals(System.IO.Path.GetFullPath(effectClipboardProjectFile),System.IO.Path.GetFullPath(projectFile!),StringComparison.OrdinalIgnoreCase)){var source=FrameTrailProjectStore.ResolveAssetPath(effectClipboardProjectFile,value.MediaPath);if(!System.IO.File.Exists(source))throw new FileNotFoundException("复制的摄像头素材不存在。",source);var asset="media/pasted-camera-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(source);var destination=FrameTrailProjectStore.ResolveAssetPath(projectFile!,asset);System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);System.IO.File.Copy(source,destination);value=value with{MediaPath=asset};}
    p.Camera=value;
   }
   else if(kind=="overlay")
   {
    var value=Read<OverlayClip>();var target=attributes?p.Overlays.First(x=>x.Id==selectedEffect!.Id):null;
    var asset=target?.MediaPath??value.MediaPath;
    if(target==null&&effectClipboardProjectFile!=null&&projectFile!=effectClipboardProjectFile){var source=FrameTrailProjectStore.ResolveAssetPath(effectClipboardProjectFile,value.MediaPath);asset="media/pasted-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(source);var destination=FrameTrailProjectStore.ResolveAssetPath(projectFile!,asset);System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);System.IO.File.Copy(source,destination);}
    var duration=target?.Duration??length;
    value=value with{MediaPath=asset,Id=pasted,Start=target?.Start??start,Duration=duration,EnterDuration=Math.Min(value.EnterDuration,duration),ExitDuration=Math.Min(value.ExitDuration,duration)};
    if(target!=null)p.Overlays.Remove(target);p.Overlays.Add(value);
   }
   else if(kind=="blur")
   {
    var value=Read<BlurClip>();var target=attributes?p.BlurClips.First(x=>x.Id==selectedEffect!.Id):null;
    var duration=target?.Duration??length;
    value=value with{Id=pasted,Start=target?.Start??start,Duration=duration,EnterDuration=Math.Min(value.EnterDuration,duration),ExitDuration=Math.Min(value.ExitDuration,duration)};
    if(target!=null)p.BlurClips.Remove(target);p.BlurClips.Add(value);
   }
  },attributes?"粘贴属性":"粘贴"+EffectLaneSupport.KindName(kind));
  if(project==null||!SelectionExists(project,new(kind,pasted)))return;
  var selection=new TimelineSelection(kind,pasted);
  SelectTimelineItem(selection);TimelineView.Refresh();
  if(!attributes&&kind!="camera")SeekToOutputTime(project.Timeline.MapSourceToOutput(start)??outputTime);else RevealTimelinePlayhead();
 }
}
