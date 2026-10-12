using System.Windows;
using System.Windows.Input;
namespace FrameTrail.App;
public partial class MainWindow
{
 private static NormalizedPoint ToCropLocalFocus(FrameTrailProject p,NormalizedPoint source){var crop=p.Settings.Canvas?.Crop??NormalizedRect.Full;return new(Math.Clamp((source.X-crop.X)/crop.Width,0,1),Math.Clamp((source.Y-crop.Y)/crop.Height,0,1));}
 private NormalizedRect? selectionRect;
 private NormalizedRect originalDragRect;
 private Point previewDragStart;
 private bool previewDragging,previewResizing;
 private int resizeCorner=-1;
 private NormalizedPoint? previewDragStartSource;
 private NormalizedRect? sourceBlurRect;
 private IReadOnlyList<NormalizedPoint>? blurSelectionQuad;
 private IReadOnlyList<NormalizedPoint>? motionSelectionQuad;
 private Rect PreviewImageBounds()
 {
  if(PreviewImage.Source==null)return Rect.Empty;var aspect=PreviewImage.Source.Width/PreviewImage.Source.Height;var w=PreviewImage.ActualWidth;var h=w/aspect;if(h>PreviewImage.ActualHeight){h=PreviewImage.ActualHeight;w=h*aspect;}return new((PreviewImage.ActualWidth-w)/2,(PreviewImage.ActualHeight-h)/2,w,h);
 }
 private void RefreshPreviewSelection()
 {
  if(project==null||SelectionVisual==null)return;selectionRect=null;motionSelectionQuad=null;blurSelectionQuad=null;sourceBlurRect=null;
  if(selectedEffect?.Kind=="motion"&&inspectorTab=="zoom"&&displayedPreviewScene is {} scene&&ReferenceEquals(project,displayedPreviewProject)){motionSelectionQuad=FrameTrail.Media.GpuFrameRenderer.GetProjectedScreenCorners(scene,previewSize.SW,previewSize.SH,previewSize.OW,previewSize.OH);var left=motionSelectionQuad.Min(p=>p.X);var top=motionSelectionQuad.Min(p=>p.Y);selectionRect=new(left,top,motionSelectionQuad.Max(p=>p.X)-left,motionSelectionQuad.Max(p=>p.Y)-top);}
  if(selectedEffect?.Kind=="overlay")selectionRect=project.Overlays.FirstOrDefault(x=>x.Id==selectedEffect.Id)?.Rect;
  if(selectedEffect?.Kind=="blur")
  {
   var clip=project.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect.Id);selectionRect=clip?.Rect;sourceBlurRect=clip?.SourceSpace==true?clip.Rect:null;
   if(clip?.SourceSpace==true&&displayedPreviewScene is {} blurScene&&ReferenceEquals(project,displayedPreviewProject)
      &&Intersects(clip.Rect,EffectiveSourceCrop(blurScene))&&TryProjectSourceRect(blurScene,clip.Rect,out var projected)){blurSelectionQuad=projected;selectionRect=Bounds(projected);}
   else if(clip?.SourceSpace==true){selectionRect=null;sourceBlurRect=null;}
  }
  if(inspectorTab=="camera"&&project.Camera is {} camera){var width=camera.Size*project.Settings.CanvasHeight/(double)project.Settings.CanvasWidth;selectionRect=CameraPlacement.RectFromPosition(camera.Position,width,camera.Size,camera.EdgeAlignedPlacement);}
  if(inspectorTab=="frame"&&frameTab=="layout"){var canvas=project.Settings.Canvas??new();var w=1-2*project.Settings.Padding;var h=w*(project.Media.Height/project.Media.Width)*(project.Settings.CanvasWidth/(double)project.Settings.CanvasHeight);if(h>w){h=w;w=h*(project.Media.Width/project.Media.Height)/(project.Settings.CanvasWidth/(double)project.Settings.CanvasHeight);}selectionRect=new(canvas.ContentPosition.X-w*canvas.ContentScale/2,canvas.ContentPosition.Y-h*canvas.ContentScale/2,w*canvas.ContentScale,h*canvas.ContentScale);}
  DrawSelection(inspectorTab=="frame"&&!previewDragging?null:selectionRect);
 }
 private void DrawSelection(NormalizedRect? rect)
 {
  var b=PreviewImageBounds();SelectionVisual.Quad=null;SelectionVisual.Selection=rect is {} r&&!b.IsEmpty?new Rect(b.X+r.X*b.Width,b.Y+r.Y*b.Height,r.Width*b.Width,r.Height*b.Height):Rect.Empty;
  if(blurSelectionQuad is {Count:4} blurQuad&&!b.IsEmpty)SelectionVisual.Quad=blurQuad.Select(p=>new Point(b.X+p.X*b.Width,b.Y+p.Y*b.Height)).ToArray();
  else if(motionSelectionQuad is {Count:4} quad&&rect is {} qr&&!b.IsEmpty){var source=previewDragging?originalDragRect:qr;SelectionVisual.Quad=quad.Select(p=>new Point(b.X+(qr.X+(p.X-source.X)*qr.Width/source.Width)*b.Width,b.Y+(qr.Y+(p.Y-source.Y)*qr.Height/source.Height)*b.Height)).ToArray();}SelectionVisual.InvalidateVisual();
 }
 private void PreviewEditDown(object sender,MouseButtonEventArgs e)
 {
  if(playing||project==null||ManualFocus.IsChecked==true&&selectedEffect?.Kind=="zoom")return;RefreshPreviewSelection();if(selectionRect is not {} r)return;DrawSelection(r);var p=e.GetPosition(PreviewImage);var visual=SelectionVisual.Selection;var corners=new[]{visual.TopLeft,visual.TopRight,visual.BottomLeft,visual.BottomRight};
  var sourceSpace=selectedEffect?.Kind=="blur"&&project.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect.Id)?.SourceSpace==true;
  resizeCorner=-1;if(SelectionVisual.Quad is {} quad&&sourceSpace){for(var i=0;i<4;i++)if((p-quad[i]).Length<14){resizeCorner=i;break;}}
  else if(SelectionVisual.Quad is {} motionQuad)resizeCorner=(p-motionQuad[2]).Length<14?3:-1;
  else resizeCorner=Array.FindIndex(corners,c=>(p-c).Length<14);
  previewResizing=resizeCorner>=0;
  if(sourceSpace){if(displayedPreviewScene is not {} scene||!ReferenceEquals(project,displayedPreviewProject)||!TryMapPreviewPoint(scene,p,out var source))return;previewDragStartSource=source;originalDragRect=sourceBlurRect??r;
   if(!previewResizing&&SelectionVisual.Quad is {Count:4} polygon&&!InsidePolygon(p,polygon)){RefreshPreviewSelection();return;}}
  else if(!visual.Contains(p)&&!previewResizing){RefreshPreviewSelection();return;}
  previewDragging=true;if(!sourceSpace)originalDragRect=r;previewDragStart=p;PreviewImage.CaptureMouse();e.Handled=true;
 }
 private void PreviewEditMove(object sender,MouseEventArgs e)
 {
  if(!previewDragging)return;var b=PreviewImageBounds();if(b.IsEmpty)return;var pointer=e.GetPosition(PreviewImage);var d=pointer-previewDragStart;
  var sourceSpace=selectedEffect?.Kind=="blur"&&project?.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect.Id)?.SourceSpace==true;
  if(sourceSpace)
  {
   if(project==null||displayedPreviewScene is not {} scene||previewDragStartSource is not {} start||!ReferenceEquals(project,displayedPreviewProject)||!TryMapPreviewPoint(scene,pointer,out var current))return;
   var deltaX=current.X-start.X;var deltaY=current.Y-start.Y;var updated=originalDragRect;
   if(previewResizing){var anchors=new[]{new NormalizedPoint(originalDragRect.X+originalDragRect.Width,originalDragRect.Y+originalDragRect.Height),new NormalizedPoint(originalDragRect.X,originalDragRect.Y+originalDragRect.Height),new NormalizedPoint(originalDragRect.X,originalDragRect.Y),new NormalizedPoint(originalDragRect.X+originalDragRect.Width,originalDragRect.Y)};var anchor=anchors[resizeCorner];var moved=new NormalizedPoint(current.X,current.Y);var left=Math.Min(anchor.X,moved.X);var top=Math.Min(anchor.Y,moved.Y);var right=Math.Max(anchor.X,moved.X);var bottom=Math.Max(anchor.Y,moved.Y);if(right-left<.01)right=Math.Min(1,left+.01);if(bottom-top<.01)bottom=Math.Min(1,top+.01);updated=new(left,top,right-left,bottom-top);}
   else updated=originalDragRect with{X=originalDragRect.X+deltaX,Y=originalDragRect.Y+deltaY};
   sourceBlurRect=ConstrainRect(updated);if(TryProjectSourceRect(scene,sourceBlurRect.Value,out var projected)){blurSelectionQuad=projected;selectionRect=Bounds(projected);}DrawSelection(selectionRect);return;
  }
  if(previewResizing){var left=resizeCorner is 0 or 2;var top=resizeCorner is 0 or 1;var width=Math.Clamp(originalDragRect.Width+(left?-1:1)*d.X/b.Width,.03,2);var height=Math.Clamp(originalDragRect.Height+(top?-1:1)*d.Y/b.Height,.03,2);if(inspectorTab is "camera" or "frame"||selectedEffect?.Kind=="motion"){height=originalDragRect.Height*width/originalDragRect.Width;}selectionRect=new(left?originalDragRect.X+originalDragRect.Width-width:originalDragRect.X,top?originalDragRect.Y+originalDragRect.Height-height:originalDragRect.Y,width,height);}else selectionRect=originalDragRect with{X=originalDragRect.X+d.X/b.Width,Y=originalDragRect.Y+d.Y/b.Height};DrawSelection(selectionRect);
 }
 private void PreviewEditUp(object sender,MouseButtonEventArgs e)
 {
  if(!previewDragging)return;PreviewEditMove(sender,e);previewDragging=false;previewDragStartSource=null;PreviewImage.ReleaseMouseCapture();if(selectionRect is not {} r)return;
  var id=selectedEffect?.Id;var kind=selectedEffect?.Kind;var isSourceBlur=kind=="blur"&&project?.BlurClips.FirstOrDefault(x=>x.Id==id)?.SourceSpace==true;var editRect=isSourceBlur&&sourceBlurRect is {} sourceRect?sourceRect:r;ApplyEdit(p=>
  {
   if(kind=="overlay")p.Overlays.First(x=>x.Id==id).Rect=ConstrainRect(r);
   else if(kind=="blur")p.BlurClips.First(x=>x.Id==id).Rect=ConstrainRect(editRect);
   else if(kind=="motion"){var target=p.ScreenMotions.First(x=>x.Id==id).Target;if(previewResizing)target.Scale=Math.Clamp(target.Scale*r.Width/originalDragRect.Width,.3,3);else target.Position=new(Math.Clamp(target.Position.X+r.X-originalDragRect.X,0,1),Math.Clamp(target.Position.Y+r.Y-originalDragRect.Y,0,1));}
   else if(inspectorTab=="camera"&&p.Camera is {} camera){camera.Position=CameraPlacement.PositionFromRect(r,camera.EdgeAlignedPlacement);if(previewResizing)camera.Size=Math.Clamp(r.Height,.08,.9);}
   else if(inspectorTab=="frame"){var c=CanvasSettingsFor(p);c.ContentPosition=new(r.X+r.Width/2,r.Y+r.Height/2);if(previewResizing)c.ContentScale=Math.Clamp(c.ContentScale*r.Width/originalDragRect.Width,.25,4);}
  });e.Handled=true;
 }

 private bool TryMapPreviewPoint(FrameScene scene,Point point,out NormalizedPoint source)
 {
  var bounds=PreviewImageBounds();if(bounds.IsEmpty){source=default;return false;}var canvas=new NormalizedPoint((point.X-bounds.X)/bounds.Width,(point.Y-bounds.Y)/bounds.Height);
  return FrameTrail.Media.GpuFrameRenderer.TryMapCanvasPointToSource(scene,previewSize.SW,previewSize.SH,previewSize.OW,previewSize.OH,canvas,out source);
 }
 private bool TryProjectSourceRect(FrameScene scene,NormalizedRect rect,out IReadOnlyList<NormalizedPoint> points)
 {
  var corners=new[]{new NormalizedPoint(rect.X,rect.Y),new NormalizedPoint(rect.X+rect.Width,rect.Y),new NormalizedPoint(rect.X+rect.Width,rect.Y+rect.Height),new NormalizedPoint(rect.X,rect.Y+rect.Height)};var result=new NormalizedPoint[4];
  for(var i=0;i<4;i++)if(!FrameTrail.Media.GpuFrameRenderer.TryMapSourcePointToCanvas(scene,previewSize.SW,previewSize.SH,previewSize.OW,previewSize.OH,corners[i],out result[i])){points=[];return false;}
  points=result;return true;
 }
 private static NormalizedRect Bounds(IReadOnlyList<NormalizedPoint> points){var left=points.Min(x=>x.X);var top=points.Min(x=>x.Y);return new(left,top,points.Max(x=>x.X)-left,points.Max(x=>x.Y)-top);}
 private static bool Intersects(NormalizedRect a,NormalizedRect b)=>a.X<b.X+b.Width&&a.X+a.Width>b.X&&a.Y<b.Y+b.Height&&a.Y+a.Height>b.Y;
 private static NormalizedRect EffectiveSourceCrop(FrameScene scene){var crop=scene.Canvas?.Crop??NormalizedRect.Full;var origin=scene.CropOrigin;var size=scene.CropSize;return new(crop.X+origin.X*crop.Width,crop.Y+origin.Y*crop.Height,size.X*crop.Width,size.Y*crop.Height);}
 private static bool InsidePolygon(Point point,IReadOnlyList<Point> polygon){var inside=false;for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++){var a=polygon[i];var b=polygon[j];if((a.Y>point.Y)!=(b.Y>point.Y)&&point.X<(b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;}return inside;}
}
