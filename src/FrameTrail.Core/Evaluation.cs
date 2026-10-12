namespace FrameTrail;

public sealed record AutoZoomOptions(double Scale = 1.6, double EnterDuration = .7, double HoldDuration = .9, double ExitDuration = .7, double GroupGap = 1.4, double SafeInset = .12, ZoomEasing Easing = ZoomEasing.Spring);

/// <summary>Plans click-centered camera regions, extends holds through continuous pointer activity, and folds touching automatic envelopes.</summary>
public static class AutoZoomPlanner
{
    public static List<ZoomClip> Plan(IEnumerable<PointerEvent> events, AutoZoomOptions? options = null)
    {
        var o = options ?? new(); ValidateOptions(o);
        var validEvents = events.Where(x => double.IsFinite(x.Time) && x.Time >= 0 && x.Location.IsFinite).OrderBy(x => x.Time).ToArray();
        var clicks = validEvents.Where(x => x.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
        var output = new List<ZoomClip>();
        var earliestNextStart = 0d;
        foreach (var group in Group(clicks, o.GroupGap))
        {
            var focusGroups = SpatialGroups(group, 1 / o.Scale * .5, 1 / o.Scale * .7);
            if (focusGroups.Count == 0) continue;
            var starts = new double[focusGroups.Count];
            starts[0] = Math.Max(earliestNextStart, Math.Max(0, focusGroups[0][0].Time - o.EnterDuration * .18));
            for (var i = 1; i < starts.Length; i++) starts[i] = Math.Max(focusGroups[i][0].Time - o.EnterDuration, starts[i - 1] + .16);
            for (var i = 0; i < focusGroups.Count; i++)
            {
                var last = i == focusGroups.Count - 1;
                var end = last ? Math.Max(focusGroups[i][^1].Time + o.HoldDuration, starts[i] + o.EnterDuration) : starts[i + 1];
                if (end <= starts[i] + .01) continue;
                var minX = focusGroups[i].Min(x => x.Location.X); var maxX = focusGroups[i].Max(x => x.Location.X);
                var minY = focusGroups[i].Min(x => x.Location.Y); var maxY = focusGroups[i].Max(x => x.Location.Y);
                output.Add(new ZoomClip { Start = starts[i], End = end, Scale = o.Scale, Focus = new NormalizedPoint((minX + maxX) * .5, (minY + maxY) * .5).Clamp(o.SafeInset), EnterDuration = o.EnterDuration, ExitDuration = last ? o.ExitDuration : 0, Easing = o.Easing, Automatic = true });
            }
            if (output.Count > 0) earliestNextStart = output[^1].End + output[^1].ExitDuration;
        }
        ExtendHolds(output, clicks, validEvents);
        return MergeTouching(output);
    }

    private static List<List<PointerEvent>> SpatialGroups(List<PointerEvent> clicks, double maxX, double maxY)
    {
        var groups = new List<List<PointerEvent>>();
        foreach (var click in clicks)
        {
            if (groups.Count == 0) { groups.Add([click]); continue; }
            var group = groups[^1];
            var lowX = Math.Min(group.Min(x => x.Location.X), click.Location.X); var highX = Math.Max(group.Max(x => x.Location.X), click.Location.X);
            var lowY = Math.Min(group.Min(x => x.Location.Y), click.Location.Y); var highY = Math.Max(group.Max(x => x.Location.Y), click.Location.Y);
            if (highX - lowX <= maxX && highY - lowY <= maxY) group.Add(click); else groups.Add([click]);
        }
        return groups;
    }
    private static void ExtendHolds(List<ZoomClip> clips, PointerEvent[] clicks, PointerEvent[] events)
    {
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i]; if (clip.ExitDuration <= .001) continue;
            var clickIndex = UpperBound(clicks, clip.End + .0001) - 1;
            if (clickIndex < 0 || clicks[clickIndex].Time < clip.Start - .0001) continue;
            var click = clicks[clickIndex];
            var nextStart = i + 1 < clips.Count ? clips[i + 1].Start : double.PositiveInfinity;
            var previous = click.Time; var activityEnd = click.Time;
            for (var eventIndex = UpperBound(events, click.Time); eventIndex < events.Length && events[eventIndex].Time < nextStart; eventIndex++)
            {
                var e = events[eventIndex];
                if (e.Time - previous > 1.4) break;
                previous = activityEnd = e.Time;
            }
            if (activityEnd <= click.Time + .0001) continue;
            clip.End = Math.Min(Math.Max(clip.End, activityEnd + .55), nextStart);
            if (double.IsFinite(nextStart)) clip.ExitDuration = Math.Min(clip.ExitDuration, Math.Max(nextStart - clip.End, 0));
        }
    }
    private static int UpperBound(PointerEvent[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid].Time <= time) low = mid + 1; else high = mid; } return low; }
    private static List<ZoomClip> MergeTouching(List<ZoomClip> clips)
    {
        clips.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<ZoomClip>();
        foreach (var clip in clips)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                if (previous.Automatic && clip.Automatic && previous.ExitDuration <= .0001 && Math.Abs(previous.End - clip.Start) <= .0001 && Math.Abs(previous.Scale - clip.Scale) <= .000001 && previous.Easing == clip.Easing)
                {
                    previous.End = Math.Max(previous.End, clip.End); previous.ExitDuration = clip.ExitDuration; continue;
                }
            }
            merged.Add(clip);
        }
        return merged;
    }
    private static IEnumerable<List<PointerEvent>> Group(PointerEvent[] clicks, double gap)
    {
        List<PointerEvent>? group = null;
        foreach (var click in clicks) { if (group is null || click.Time - group[^1].Time > gap) { if (group is not null) yield return group; group = []; } group.Add(click); }
        if (group is not null) yield return group;
    }
    private static void ValidateOptions(AutoZoomOptions o)
    {
        if (!double.IsFinite(o.Scale) || o.Scale < 1 || o.Scale > 20 || !double.IsFinite(o.EnterDuration) || o.EnterDuration is < 0 or > 5 || !double.IsFinite(o.HoldDuration) || o.HoldDuration < 0 || !double.IsFinite(o.ExitDuration) || o.ExitDuration is < 0 or > 5 || !double.IsFinite(o.GroupGap) || o.GroupGap < 0 || !double.IsFinite(o.SafeInset) || o.SafeInset is < 0 or >= .5) throw new ArgumentOutOfRangeException(nameof(o), "自动缩放参数超出范围。");
    }
}

public static class PointerTrack
{
    public static (NormalizedPoint? Position, double ClickStrength) Sample(IReadOnlyList<PointerEvent> source, double sourceTime, PointerSmoothing smoothing = PointerSmoothing.Smooth)
        => new PreparedPointerTrack(source, smoothing).Sample(sourceTime);
}

/// <summary>Pre-sorted pointer samples; each frame uses binary search instead of sorting and scanning the recording.</summary>
public sealed class PreparedPointerTrack
{
    private readonly PointerEvent[] events;
    private readonly PointerEvent[] clicks;
    private readonly PointerSmoothing smoothing;
    private readonly SpringSample[] springSamples;
    private readonly double[] restartTimes;
    private readonly double springMass = 2.8, springStiffness = 450, springDamping = 66;
    /// <param name="spring">Optional pointer spring (Mac CursorStyle spring mass/stiffness/damping, default 2.8/450/66).</param>
    /// <param name="restartTimes">Pointer-clock times where a retained segment resumes after a cut; the spring is re-seeded there.</param>
    public PreparedPointerTrack(IEnumerable<PointerEvent> source, PointerSmoothing smoothing = PointerSmoothing.Smooth, IEnumerable<double>? restartTimes = null,
        (double Mass, double Stiffness, double Damping)? spring = null)
    {
        if (spring is { } custom && double.IsFinite(custom.Mass) && double.IsFinite(custom.Stiffness) && double.IsFinite(custom.Damping))
        { springMass = Math.Clamp(custom.Mass, .2, 20); springStiffness = Math.Clamp(custom.Stiffness, 20, 4000); springDamping = Math.Clamp(custom.Damping, 4, 400); }
        if (!Enum.IsDefined(smoothing)) throw new ArgumentOutOfRangeException(nameof(smoothing));
        this.smoothing = smoothing;
        this.restartTimes = (restartTimes ?? []).Where(double.IsFinite).Distinct().OrderBy(x => x).ToArray();
        events = source.Where(x => double.IsFinite(x.Time) && x.Time >= 0 && x.Location.IsFinite).OrderBy(x => x.Time).ToArray();
        if (events.Length > 0 && events[^1].Time > TimeSpan.FromDays(7).TotalSeconds)
            throw new InvalidDataException("鼠标事件时间超出七天安全范围，无法构建平滑轨道。");
        clicks = events.Where(x => x.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
        springSamples = smoothing == PointerSmoothing.Smooth ? BuildSpringSamples() : [];
    }
    public (NormalizedPoint? Position, double ClickStrength) Sample(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0 || events.Length == 0) return (null, 0);
        var upper = UpperBound(events, sourceTime);
        if (upper == 0) return (null, 0);
        var lowerIndex = upper - 1; var lower = events[lowerIndex];
        var location = smoothing switch
        {
            PointerSmoothing.Smooth => SampleSpring(sourceTime),
            PointerSmoothing.None => SampleRaw(lowerIndex, upper, sourceTime),
            _ => SampleHermite(lowerIndex, upper, sourceTime)
        };
        var clickIndex = UpperBound(clicks, sourceTime) - 1;
        var click = clickIndex >= 0 && sourceTime - clicks[clickIndex].Time <= .18 ? clicks[clickIndex] : null;
        var strength = click is null ? 0 : Math.Sin(Math.PI * Math.Clamp((sourceTime - click.Time) / .18, 0, 1));
        return (location, strength);
    }
    public double SecondsSinceLastEvent(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0 || events.Length == 0) return double.PositiveInfinity;
        var index = UpperBound(events, sourceTime) - 1;
        return index < 0 ? double.PositiveInfinity : Math.Max(0, sourceTime - events[index].Time);
    }
    public string? CursorAssetAt(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0 || events.Length == 0) return null;
        var index = UpperBound(events, sourceTime) - 1;
        return index < 0 ? null : events[index].CursorAsset;
    }
    private static int UpperBound(PointerEvent[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid].Time <= time) low = mid + 1; else high = mid; } return low; }
    private static double Tangent(double t0, double v0, double t1, double v1, double t2, double v2)
    {
        var d0 = t1 - t0; var d1 = t2 - t1; if (d0 <= 0 || d1 <= 0) return 0;
        var s0 = (v1 - v0) / d0; var s1 = (v2 - v1) / d1; if (s0 * s1 <= 0) return 0;
        var w0 = 2 * d1 + d0; var w1 = d1 + 2 * d0;
        return (w0 + w1) / (w0 / s0 + w1 / s1);
    }
    private static double Hermite(double a, double b, double ma, double mb, double duration, double t)
    { var t2 = t * t; var t3 = t2 * t; return (2*t3-3*t2+1)*a + (t3-2*t2+t)*duration*ma + (-2*t3+3*t2)*b + (t3-t2)*duration*mb; }

    private NormalizedPoint SampleHermite(int lowerIndex, int upperIndex, double time)
    {
        var lower = events[lowerIndex];
        if (upperIndex >= events.Length) return lower.Location;
        var upper = events[upperIndex];
        var duration = upper.Time - lower.Time;
        if (duration <= 0 || !IsContinuousMovement(lower, upper)) return lower.Location;
        var progress = Math.Clamp((time - lower.Time) / duration, 0, 1);
        var tangentScale = smoothing == PointerSmoothing.Medium ? .78 : 1;
        return new NormalizedPoint(
            Hermite(lower.Location.X, upper.Location.X, TangentAt(lowerIndex, true) * tangentScale, TangentAt(upperIndex, true) * tangentScale, duration, progress),
            Hermite(lower.Location.Y, upper.Location.Y, TangentAt(lowerIndex, false) * tangentScale, TangentAt(upperIndex, false) * tangentScale, duration, progress)).Clamp();
    }

    /// <summary>Recorded trajectory without reconstruction (Mac CursorMotionStyle.none): straight segments between samples, held across long gaps.</summary>
    private NormalizedPoint SampleRaw(int lowerIndex, int upperIndex, double time)
    {
        var lower = events[lowerIndex];
        if (upperIndex >= events.Length) return lower.Location;
        var upper = events[upperIndex];
        var duration = upper.Time - lower.Time;
        if (duration <= 0 || !IsContinuousMovement(lower, upper)) return lower.Location;
        var progress = Math.Clamp((time - lower.Time) / duration, 0, 1);
        return new NormalizedPoint(lower.Location.X + (upper.Location.X - lower.Location.X) * progress,
            lower.Location.Y + (upper.Location.Y - lower.Location.Y) * progress).Clamp();
    }

    private NormalizedPoint RawLocationAt(double time)
    {
        var upper = UpperBound(events, time);
        if (upper == 0) return events[0].Location;
        return SampleRaw(upper - 1, upper, time);
    }

    private double TangentAt(int index, bool xAxis)
    {
        var current = events[index];
        var previousIndex = index - 1;
        while (previousIndex >= 0 && events[previousIndex].Time >= current.Time) previousIndex--;
        var nextIndex = index + 1;
        while (nextIndex < events.Length && events[nextIndex].Time <= current.Time) nextIndex++;
        var currentValue = xAxis ? current.Location.X : current.Location.Y;
        if (previousIndex >= 0 && nextIndex < events.Length)
        {
            var previous = events[previousIndex]; var next = events[nextIndex];
            var previousDuration = current.Time - previous.Time; var nextDuration = next.Time - current.Time;
            var previousValue = xAxis ? previous.Location.X : previous.Location.Y;
            var nextValue = xAxis ? next.Location.X : next.Location.Y;
            var previousSlope = (currentValue - previousValue) / previousDuration;
            var nextSlope = (nextValue - currentValue) / nextDuration;
            if (!double.IsFinite(previousSlope) || !double.IsFinite(nextSlope) || previousSlope * nextSlope <= 0) return 0;
            var firstWeight = 2 * nextDuration + previousDuration;
            var secondWeight = nextDuration + 2 * previousDuration;
            var denominator = firstWeight / previousSlope + secondWeight / nextSlope;
            return double.IsFinite(denominator) && Math.Abs(denominator) > double.Epsilon
                ? (firstWeight + secondWeight) / denominator : 0;
        }
        if (nextIndex < events.Length)
        {
            var next = events[nextIndex];
            return ((xAxis ? next.Location.X : next.Location.Y) - currentValue) / (next.Time - current.Time);
        }
        if (previousIndex >= 0)
        {
            var previous = events[previousIndex];
            return (currentValue - (xAxis ? previous.Location.X : previous.Location.Y)) / (current.Time - previous.Time);
        }
        return 0;
    }

    private NormalizedPoint SampleSpring(double time)
    {
        var basePosition = SampleSpringBase(time);
        var clickIndex = UpperBound(clicks, time);
        var index = clickIndex < clicks.Length && time >= clicks[clickIndex].Time - .18
            ? clickIndex
            : clickIndex - 1;
        if (index < 0 || index >= clicks.Length) return basePosition;
        var click = clicks[index];
        var previous = index > 0 ? clicks[index - 1].Time : double.NegativeInfinity;
        var next = index + 1 < clicks.Length ? clicks[index + 1].Time : double.PositiveInfinity;
        var start = Math.Max(events[0].Time, Math.Max(click.Time - .18, double.IsNegativeInfinity(previous) ? double.NegativeInfinity : (previous + click.Time) * .5));
        var end = Math.Min(click.Time + .18, double.IsPositiveInfinity(next) ? double.PositiveInfinity : (click.Time + next) * .5);
        if (time < start || time > end || end <= start) return basePosition;
        var anchor = SampleSpringBase(click.Time);
        var dx = click.Location.X - anchor.X; var dy = click.Location.Y - anchor.Y;
        var weight = time <= click.Time ? SmootherStep((time - start) / Math.Max(click.Time - start, double.Epsilon))
            : 1 - SmootherStep((time - click.Time) / Math.Max(end - click.Time, double.Epsilon));
        return new NormalizedPoint(Math.Clamp(basePosition.X + dx * weight, 0, 1), Math.Clamp(basePosition.Y + dy * weight, 0, 1));
    }

    private NormalizedPoint SampleSpringBase(double time)
    {
        var i = UpperBound(springSamples, time);
        if (i == 0) return events[0].Location;
        if (i >= springSamples.Length) return springSamples[^1].Position;
        var a = springSamples[i - 1]; var b = springSamples[i];
        var t = Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1);
        return new NormalizedPoint(a.Position.X + (b.Position.X - a.Position.X) * t, a.Position.Y + (b.Position.Y - a.Position.Y) * t);
    }

    private SpringSample[] BuildSpringSamples()
    {
        if (events.Length == 0) return [];
        const double step = 1d / 240;
        double mass = springMass, stiffness = springStiffness, damping = springDamping;
        var first = events[0]; var last = events[^1];
        var settleDuration = Math.Max(.65, 6 * Math.Sqrt(mass / stiffness));
        var endTime = last.Time + settleDuration;
        var estimatedSamples = Math.Ceiling((endTime - first.Time) / step) + 2;
        var samples = new List<SpringSample>((int)Math.Min(estimatedSamples, 65_536)) { new(first.Time, first.Location) };
        var position = first.Location; var stableTarget = position; var velocityX = 0d; var velocityY = 0d;
        var eventIndex = 1; var sampleTime = first.Time; var restartIndex = 0;
        while (sampleTime < endTime)
        {
            if (samples.Count >= 2_000_000)
                throw new InvalidDataException("鼠标轨道过长，平滑缓存已达到安全上限；请将移动平滑切换为“适中”或“灵敏”，或使用较短的指针素材。");
            if (eventIndex < events.Length)
            {
                var previousEvent = events[eventIndex - 1];
                var nextEvent = events[eventIndex];
                if (nextEvent.Time - previousEvent.Time > 1.3
                    && sampleTime >= previousEvent.Time + .65
                    && sampleTime < nextEvent.Time - .65)
                {
                    sampleTime = nextEvent.Time - .65;
                    samples.Add(new(sampleTime, position));
                    continue;
                }
            }
            var nextTime = Math.Min(sampleTime + step, endTime);
            while (restartIndex < restartTimes.Length && restartTimes[restartIndex] <= sampleTime + 1e-9) restartIndex++;
            var restartAtNext = restartIndex < restartTimes.Length && restartTimes[restartIndex] <= nextTime;
            if (restartAtNext) nextTime = restartTimes[restartIndex];
            while (eventIndex < events.Length && events[eventIndex].Time <= nextTime) eventIndex++;
            var lowerIndex = Math.Max(eventIndex - 1, 0);
            NormalizedPoint target;
            if (eventIndex < events.Length)
            {
                var lo = events[lowerIndex]; var hi = events[eventIndex];
                if (IsContinuousMovement(lo, hi))
                {
                    var t = Math.Clamp((nextTime - lo.Time) / Math.Max(hi.Time - lo.Time, double.Epsilon), 0, 1);
                    target = new(lo.Location.X + (hi.Location.X - lo.Location.X) * t, lo.Location.Y + (hi.Location.Y - lo.Location.Y) * t);
                }
                else target = lo.Location;
            }
            else target = last.Location;
            var dx = target.X - stableTarget.X; var dy = target.Y - stableTarget.Y;
            if (dx * dx + dy * dy >= .00035 * .00035) stableTarget = target;
            var dt = nextTime - sampleTime;
            velocityX += (stiffness * (stableTarget.X - position.X) - damping * velocityX) / mass * dt;
            velocityY += (stiffness * (stableTarget.Y - position.Y) - damping * velocityY) / mass * dt;
            position = new(Math.Clamp(position.X + velocityX * dt, 0, 1), Math.Clamp(position.Y + velocityY * dt, 0, 1));
            sampleTime = nextTime; samples.Add(new(sampleTime, position));
            if (restartAtNext)
            {
                // The retained clip resumes here: start the spring at the recorded position instead of replaying the deleted movement.
                position = RawLocationAt(sampleTime); stableTarget = position; velocityX = 0; velocityY = 0;
                sampleTime += 1e-6; samples.Add(new(sampleTime, position)); restartIndex++;
            }
        }
        return samples.ToArray();
    }

    private static bool IsContinuousMovement(PointerEvent lower, PointerEvent upper)
    {
        var duration = upper.Time - lower.Time;
        if (duration <= 0) return false;
        if (duration <= .12) return true;
        if (duration > .50) return false;
        var dx = upper.Location.X - lower.Location.X; var dy = upper.Location.Y - lower.Location.Y;
        return dx * dx + dy * dy <= .03 * .03;
    }
    private static double SmootherStep(double x) { x = Math.Clamp(x, 0, 1); return x * x * x * (x * (x * 6 - 15) + 10); }
    private static int UpperBound(SpringSample[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid].Time <= time) low = mid + 1; else high = mid; } return low; }
    private readonly record struct SpringSample(double Time, NormalizedPoint Position);
}

public static class FrameSceneEvaluator
{
    public static FrameScene Evaluate(FrameTrailProject project, double outputTime)
        => new PreparedFrameSceneEvaluator(project).Evaluate(outputTime);
}

/// <summary>Build once per export or preview session; per-frame work maps segments and samples tracks with binary search.</summary>
public sealed class PreparedFrameSceneEvaluator
{
    private readonly PreparedOverlayTrack overlayTrack;
    private readonly FrameTrailProject project;
    private readonly TimelineSegment[] segments;
    private readonly double[] outputEnds;
    private readonly ZoomClip[] clips;
    private readonly double[] clipStarts;
    private readonly double[] clipPrefixEnd;
    private readonly PreparedPointerTrack pointerTrack;
    private readonly PreparedPointerDynamics pointerDynamics;
    private readonly PreparedAutoFollowTrack?[] autoFollowTracks;
    private readonly ScreenMotionClip[] motionClips;
    private readonly double[] motionStarts;
    private readonly OverlayClip[] overlays;
    private readonly BlurClip[] blurClips;
    private readonly CameraMotionClip[] cameraMotions;
    private readonly CameraSyncSegment[] cameraSyncSegments;
    private readonly double[] cameraMotionStarts;
    private readonly NormalizedRect canvasCrop;
    private readonly bool[] segmentFollowsCut;
    private readonly double[] segmentFirstClickOutput;
    private readonly CameraMotionPose? cameraBasePose;
    private readonly CameraMotionPose?[] cameraMotionFromStates;
    private readonly CameraMotionPose?[] cameraMotionTargets;
    private readonly double cameraCanvasAspect;
    public double Duration => outputEnds[^1];

    public PreparedFrameSceneEvaluator(FrameTrailProject project, double? cameraAssetAspectRatioOverride = null)
    {
        ArgumentNullException.ThrowIfNull(project); project.Timeline.Validate();
        if (cameraAssetAspectRatioOverride is { } measuredAspect && (!double.IsFinite(measuredAspect) || measuredAspect <= 0 || measuredAspect > 100))
            throw new ArgumentOutOfRangeException(nameof(cameraAssetAspectRatioOverride));
        if (!double.IsFinite(project.MouseClock.OffsetSeconds) || !double.IsFinite(project.MouseClock.Rate) || project.MouseClock.Rate <= 0) throw new InvalidDataException("鼠标时间校准参数无效。");
        var spring = project.Settings.Spring;
        if (spring is null || !double.IsFinite(spring.Mass) || spring.Mass is < .01 or > 100
            || !double.IsFinite(spring.Stiffness) || spring.Stiffness is < .01 or > 10000
            || !double.IsFinite(spring.Damping) || spring.Damping is < .01 or > 1000)
            throw new InvalidDataException("动画弹簧参数无效。");
        this.project = project;
        canvasCrop = (project.Settings.Canvas?.Crop ?? NormalizedRect.Full).Clamp();
        segments = project.Timeline.Segments.ToArray(); outputEnds = new double[segments.Length];
        var output = 0d;
        for (var i = 0; i < segments.Length; i++) outputEnds[i] = output += segments[i].OutputDuration;
        // Retained segments that start after a gap in the source (a cut) re-seed the pointer spring instead of letting it
        // glide through the deleted footage (Mac TimelineMap.mapPointerEventSegments / ProjectPointerTrack cut seeds).
        segmentFollowsCut = new bool[segments.Length];
        for (var i = 1; i < segments.Length; i++) segmentFollowsCut[i] = Math.Abs(segments[i].SourceStart - segments[i - 1].SourceEnd) > 1e-4;
        var restartTimes = Enumerable.Range(1, Math.Max(segments.Length - 1, 0)).Where(i => segmentFollowsCut[i])
            .Select(i => (segments[i].SourceStart - project.MouseClock.OffsetSeconds) / project.MouseClock.Rate).Where(double.IsFinite).ToArray();
        var pointerSpring = project.Settings.Cursor is { } springCursor ? (springCursor.SpringMass, springCursor.SpringStiffness, springCursor.SpringDamping) : ((double, double, double)?)null;
        pointerTrack = new PreparedPointerTrack(project.PointerEvents, project.Settings.PointerSmoothing, restartTimes, pointerSpring);
        pointerDynamics = new PreparedPointerDynamics(pointerTrack, project.PointerEvents);
        segmentFirstClickOutput = new double[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var firstClick = double.PositiveInfinity;
            foreach (var e in project.PointerEvents)
            {
                if (e.Kind is not (PointerEventKind.LeftClick or PointerEventKind.RightClick) || !double.IsFinite(e.Time) || e.Time < 0) continue;
                var clickSource = project.MouseClock.ToSourceTime(e.Time);
                if (clickSource >= segments[i].SourceStart && clickSource < segments[i].SourceEnd) firstClick = Math.Min(firstClick, (clickSource - segments[i].SourceStart) / segments[i].PlaybackRate);
            }
            segmentFirstClickOutput[i] = firstClick;
        }
        // Effect transitions are authored in output seconds; convert them to source seconds with the playback rate of the
        // segment they run in so a 0.7 s transition stays 0.7 s in a 4x or 0.5x segment (Mac keeps effects on the output clock).
        motionClips = project.ScreenMotions.OrderBy(x => x.Start).Select(x => x with { EnterDuration = x.EnterDuration * RateAfter(x.Start), ReturnDuration = x.ReturnDuration * RateAfter(x.End) }).ToArray();
        motionStarts = motionClips.Select(x => x.Start).ToArray();
        overlays = project.Overlays.OrderBy(x => x.Layer).ThenBy(x => x.Start)
            .Select(x => x with { EnterDuration = x.EnterDuration * RateAfter(x.Start), ExitDuration = x.ExitDuration * RateBefore(x.End) }).ToArray();
        overlayTrack = new(overlays);
        blurClips = project.BlurClips.OrderBy(x => x.Start)
            .Select(x => x with { EnterDuration = x.EnterDuration * RateAfter(x.Start), ExitDuration = x.ExitDuration * RateBefore(x.End) }).ToArray();
        cameraMotions = project.CameraMotions.OrderBy(x => x.Start)
            .Select(x => x with { EnterDuration = x.EnterDuration * RateAfter(x.Start), ReturnDuration = x.ReturnDuration * RateAfter(x.End) }).ToArray();
        cameraSyncSegments = project.Camera?.SyncSegments?.OrderBy(x => x.TimelineStart).ToArray() ?? [];
        cameraMotionStarts = cameraMotions.Select(x => x.Start).ToArray();
        var canvasSettings = project.Settings.Canvas;
        cameraCanvasAspect = (canvasSettings?.AspectWidth ?? project.Settings.CanvasWidth)
            / (double)Math.Max(2, canvasSettings?.AspectHeight ?? project.Settings.CanvasHeight);
        var cameraCrop = project.Camera?.Crop ?? NormalizedRect.Full;
        var baseAssetAspect = cameraAssetAspectRatioOverride ?? project.Camera?.AssetAspectRatio ?? 1;
        var cameraAspect = baseAssetAspect * Math.Max(cameraCrop.Width, .001) / Math.Max(cameraCrop.Height, .001);
        cameraBasePose = CameraMotionEvaluator.Base(project.Camera, project.Camera?.Layout, cameraAspect, cameraCanvasAspect);
        cameraMotionFromStates = new CameraMotionPose?[cameraMotions.Length];
        cameraMotionTargets = new CameraMotionPose?[cameraMotions.Length];
        var baseRoundness = project.Camera is { } baseCamera
            ? baseCamera.Layout is not null
                ? Math.Clamp(baseCamera.CornerRadius, 0, 1)
                : baseCamera.Size > 1e-9 ? Math.Clamp(2 * baseCamera.CornerRadius / baseCamera.Size, 0, 1) : 0
            : 0;
        for (var i = 0; i < cameraMotions.Length; i++)
        {
            var motion = cameraMotions[i];
            var targetLayout = motion.TargetLayout ?? (motion.TargetRoundness is not null
                ? CameraMotionLayoutKind.RoundedRectangle : project.Camera?.Layout);
            cameraMotionTargets[i] = CameraMotionEvaluator.Target(project.Camera, motion.TargetPosition, motion.TargetSize,
                targetLayout, motion.TargetRoundness ?? baseRoundness, motion.TargetOpacity ?? project.Camera?.Opacity ?? 1,
                cameraAspect, cameraCanvasAspect);
        }
        for (var i = 0; i < cameraMotions.Length; i++)
        {
            if (i == 0) cameraMotionFromStates[i] = cameraBasePose;
            else
            {
                var previous = cameraMotions[i - 1];
                var destination = cameraMotions[i].Start <= previous.End + previous.ReturnDuration
                    ? cameraMotionTargets[i] : cameraBasePose;
                cameraMotionFromStates[i] = EvaluateCameraMotionClip(i - 1, cameraMotions[i].Start, cameraMotionFromStates[i - 1], destination);
            }
        }
        var sourceClips = project.ZoomClips.Count > 0
            ? project.ZoomClips
            : project.Settings.AutoZoom
                ? AutoZoomPlanner.Plan(project.PointerEvents.Select(e => e with { Time = project.MouseClock.ToSourceTime(e.Time) }))
                : [];
        // Automatic clip focuses originate in full-source pointer coordinates. ZoomClip.Focus
        // is otherwise canvas-crop-local, so normalize automatic focuses once for evaluation.
        clips = sourceClips.Select(x => (x.Automatic ? x with { Focus = ToCanvasCropLocal(x.Focus) } : x)
                with { EnterDuration = x.EnterDuration * RateAfter(x.Start), ExitDuration = x.ExitDuration * RateAfter(x.End) })
            .OrderBy(x => x.Start).ToArray(); clipStarts = clips.Select(x => x.Start).ToArray(); clipPrefixEnd = new double[clips.Length];
        var calibratedPointerEvents = project.PointerEvents
            .Where(e => double.IsFinite(e.Time) && e.Time >= 0 && e.Location.IsFinite)
            .Select(e => e with { Time = project.MouseClock.ToSourceTime(e.Time) })
            .OrderBy(e => e.Time).ToArray();
        autoFollowTracks = new PreparedAutoFollowTrack?[clips.Length];
        var remainingFollowSamples = PreparedAutoFollowTrack.MaximumTotalSamples;
        var cutGaps = new List<(double Start, double End)>();
        for (var i = 1; i < segments.Length; i++)
            if (segmentFollowsCut[i] && segments[i].SourceStart > segments[i - 1].SourceEnd) cutGaps.Add((segments[i - 1].SourceEnd, segments[i].SourceStart));
        for (var i = 0; i < clips.Length; i++)
            if (clips[i].Automatic)
            {
                autoFollowTracks[i] = PreparedAutoFollowTrack.Build(clips[i], calibratedPointerEvents, project.Settings.Spring, canvasCrop, remainingFollowSamples, cutGaps);
                remainingFollowSamples -= autoFollowTracks[i]!.SampleCount;
            }
        var maxEnd = double.NegativeInfinity;
        for (var i = 0; i < clips.Length; i++) clipPrefixEnd[i] = maxEnd = Math.Max(maxEnd, clips[i].End + clips[i].ExitDuration);
    }

    public FrameScene Evaluate(double outputTime)
    {
        if (!double.IsFinite(outputTime) || outputTime < 0 || outputTime > Duration) throw new ArgumentOutOfRangeException(nameof(outputTime));
        var sourceTime = MapOutputToSource(outputTime);
        var zoomSample = ZoomSampleState.Identity;
        var zoom = 1d; var focus = new NormalizedPoint(.5, .5);
        ZoomClip? activeZoomClip = null;
        var upperClip = UpperBound(clipStarts, sourceTime);
        for (var i = upperClip - 1; i >= 0 && clipPrefixEnd[i] >= sourceTime; i--)
        {
            var clip = clips[i]; if (sourceTime < clip.Start || sourceTime > clip.End + clip.ExitDuration) continue;
            var adjacentPrevious = i > 0 && Math.Abs(clips[i - 1].End - clip.Start) <= 1d / 120
                ? clips[i - 1] : null;
            // Mac ZoomInterpolator: an adjacent clip stored with a zero entrance still hands off over the project default (0.7 s output).
            var requestedEnterDuration = adjacentPrevious is not null && clip.EnterDuration <= .001 ? .7 * RateAfter(clip.Start) : clip.EnterDuration;
            var effectiveEnterDuration = Math.Min(requestedEnterDuration, Math.Max(clip.End - clip.Start, 0));
            var enterLocal = effectiveEnterDuration <= 0 ? 1 : Math.Clamp((sourceTime - clip.Start) / effectiveEnterDuration, 0, 1);
            var enterOffset = Math.Clamp(clip.EnterProgressOffset, 0, 1);
            var enter = effectiveEnterDuration <= 0 ? 1 : Ease(enterOffset + (1 - enterOffset) * enterLocal, clip.Easing, clip.CustomCurve, project.Settings.Spring);
            var exitLocal = clip.ExitDuration <= 0 ? 1 : Math.Clamp((sourceTime - clip.End) / clip.ExitDuration, 0, 1);
            var exitOffset = Math.Clamp(clip.ExitProgressOffset, 0, 1);
            var exit = sourceTime <= clip.End || clip.ExitDuration <= 0 ? 1 : 1 - Ease(exitOffset + (1 - exitOffset) * exitLocal, clip.Easing, clip.CustomCurve, project.Settings.Spring);
            var progress = Math.Min(enter, exit);
            var targetFocus = autoFollowTracks[i]?.Sample(sourceTime) ?? clip.Focus;
            if (adjacentPrevious is not null && sourceTime <= clip.End)
            {
                // Swift inherits the previous viewport only during the incoming clip's entry and hold. Its return phase
                // always interpolates to the base canvas zoom instead of returning to the prior clip.
                var fromScale = adjacentPrevious.Scale;
                var fromFocus = autoFollowTracks[i - 1]?.Sample(adjacentPrevious.End) ?? adjacentPrevious.Focus;
                zoom = fromScale + (clip.Scale - fromScale) * progress;
                focus = new(fromFocus.X + (targetFocus.X - fromFocus.X) * progress,
                    fromFocus.Y + (targetFocus.Y - fromFocus.Y) * progress);
                // Manual hand-offs interpolate between the two already-framed viewports (Mac ManualZoomViewportTransition);
                // automatic hand-offs blend scale and focus before framing.
                zoomSample = new ZoomSampleState(zoom, focus, 1, clip.Automatic ? null
                    : new ManualZoomViewportTransition(fromScale, fromFocus, clip.Scale, targetFocus, progress));
            }
            else
            {
                zoom = 1 + (clip.Scale - 1) * progress;
                focus = targetFocus;
                zoomSample = new ZoomSampleState(zoom, focus, progress);
            }
            activeZoomClip = clip;
            break;
        }
        var pointerTime = (sourceTime - project.MouseClock.OffsetSeconds) / project.MouseClock.Rate;
        var pointer = pointerTrack.Sample(pointerTime);
        pointer = (BridgeCut(outputTime, sourceTime, pointer.Position), pointer.ClickStrength);
        // The renderer no longer crops the source inside a fixed card: it magnifies the whole card from ZoomSample (Mac ZoomViewportTransform).
        // CropOrigin/CropSize keep describing the unframed focus window (focus-centred, clamped to the source) for probes and legacy consumers;
        // geometry that must match the picture goes through GpuFrameRenderer's mapping functions instead.
        var cropSize = 1 / Math.Max(zoom, 1);
        var origin = new NormalizedPoint(Math.Clamp(focus.X - cropSize / 2, 0, 1 - cropSize), Math.Clamp(focus.Y - cropSize / 2, 0, 1 - cropSize));
        var cursorSettings = project.Settings.Cursor ?? new CursorSettings { Visible = project.Settings.CursorVisible };
        var cursorVisible = project.Settings.CursorVisible && cursorSettings.Visible;
        if (cursorSettings.HideWhenIdle && pointerTrack.SecondsSinceLastEvent(pointerTime) > cursorSettings.IdleSeconds) cursorVisible = false;
        var cursor = cursorVisible ? pointer.Position : null;
        var dynamics = pointerDynamics.Sample(pointerTime, project.Settings.PointerSmoothing,
            cursorSettings.MotionTiltEnabled ? cursorSettings.MotionTiltStrength : 0, cursorSettings.ClickDuration * RateAtSource(sourceTime));
        var clickProgress = cursorVisible && cursorSettings.ClickEffectsEnabled ? dynamics.ClickProgress : null;
        var strength = clickProgress is { } click ? Math.Sin(click * Math.PI) : 0;
        var s = project.Settings;
        var canvas = s.Canvas ?? new CanvasSettings { AspectWidth = s.CanvasWidth, AspectHeight = s.CanvasHeight };
        var background = s.BackgroundStyle ?? new BackgroundSettings { Color = s.Background, EndColor = s.Background };
        var (activeMotion, screenBlend) = EvaluateScreenMotion(sourceTime);
        var initialSourceTime = MapOutputToSource(0);
        var screenOpening = EvaluateOpening(outputTime, initialSourceTime, OpeningElement.Screen);
        var cameraOpening = EvaluateOpening(outputTime, initialSourceTime, OpeningElement.Camera);
        var overlayOpening = EvaluateOpening(outputTime, initialSourceTime, OpeningElement.Overlays);
        var camera = EvaluateCamera(sourceTime, cameraOpening, zoom);
        var activeOverlays = EvaluateOverlays(sourceTime, overlayOpening, initialSourceTime, out var overlayBackdrop);
        var blurRegions = EvaluateBlurRegions(sourceTime);
        var (resolvedFocus, focusProgress, focusAnchor, _) = EvaluateFocus(sourceTime, activeZoomClip, focus);
        if (resolvedFocus is { } focusEffect)
        {
            var center = focusEffect.Target switch
            {
                FocusEffectTarget.Pointer => pointer.Position ?? focusAnchor,
                FocusEffectTarget.Animation => focusAnchor,
                _ => focusEffect.Center
            };
            resolvedFocus = focusEffect with { Center = center };
        }
        return new FrameScene(outputTime, sourceTime, origin, new(cropSize, cropSize), zoom, cursor, strength, s.Background, s.Padding, s.CornerRadius, s.CanvasWidth, s.CanvasHeight, s.Shadow,
            canvas, background, cursorSettings, activeMotion, resolvedFocus, focusProgress, camera, activeOverlays, blurRegions, cameraMotions,
            (int)Math.Max(0, Math.Round(project.Media.Width)), (int)Math.Max(0, Math.Round(project.Media.Height)), screenOpening, cameraOpening, overlayOpening,
            cursorVisible ? dynamics.RotationRadians : 0, clickProgress, clickProgress.HasValue ? dynamics.ActiveClickKind : null,
            pointerTrack.CursorAssetAt(pointerTime), overlayBackdrop, zoomSample, screenBlend);
    }

    /// <summary>Returns the scene at outputTime with a selected zoom clip's target crop applied immediately.</summary>
    public FrameScene EvaluateTargetForZoom(Guid zoomId, double outputTime)
    {
        var clip = clips.FirstOrDefault(x => x.Id == zoomId) ?? throw new KeyNotFoundException($"找不到缩放片段 {zoomId}。");
        var scene = Evaluate(outputTime);
        var scale = Math.Max(1, clip.Scale);
        var focus = clip.Focus;
        var cropSize = 1 / scale;
        var origin = new NormalizedPoint(Math.Clamp(focus.X - cropSize / 2, 0, 1 - cropSize), Math.Clamp(focus.Y - cropSize / 2, 0, 1 - cropSize));
        var (screenEffect, screenProgress, screenAnchor, screenSelected) = EvaluateFocus(scene.SourceTime, null, focus);
        var focusEffect = screenSelected ? screenEffect : clip.FocusEffect;
        var focusProgress = screenSelected ? screenProgress : focusEffect is not null ? 1 : 0;
        var focusAnchor = screenSelected ? screenAnchor : new NormalizedPoint(canvasCrop.X + focus.X * canvasCrop.Width,
            canvasCrop.Y + focus.Y * canvasCrop.Height);
        if (focusEffect is { } effect)
        {
            var pointerTime = (scene.SourceTime - project.MouseClock.OffsetSeconds) / project.MouseClock.Rate;
            var center = effect.Target switch
            {
                FocusEffectTarget.Pointer => pointerTrack.Sample(pointerTime).Position ?? focusAnchor,
                FocusEffectTarget.Animation => focusAnchor,
                _ => effect.Center
            };
            focusEffect = effect with { Center = center };
        }
        return scene with { CropOrigin = origin, CropSize = new(cropSize, cropSize), Zoom = scale, ZoomSample = new ZoomSampleState(scale, focus, 1), Focus = focusEffect, FocusProgress = focusProgress };
    }

    private NormalizedPoint ToCanvasCropLocal(NormalizedPoint fullSource) => new(
        canvasCrop.Width > 0 ? (fullSource.X - canvasCrop.X) / canvasCrop.Width : .5,
        canvasCrop.Height > 0 ? (fullSource.Y - canvasCrop.Y) / canvasCrop.Height : .5);

    public ScreenMotionState EvaluateTargetForMotion(Guid motionId)
    {
        var clip = motionClips.FirstOrDefault(x => x.Id == motionId) ?? throw new KeyNotFoundException($"找不到屏幕运动片段 {motionId}。");
        return clip.Target with { Position = clip.Target.Position with { } };
    }

    /// <summary>Samples the resolved screen motion on the original source clock for timeline editing.</summary>
    internal ScreenMotionState SampleScreenMotionAtSourceTime(double sourceTime)
    {
        ValidateSourceSampleTime(sourceTime);
        var canvas = project.Settings.Canvas;
        var state = EvaluateScreenMotion(sourceTime).State ?? new ScreenMotionState
        {
            Position = canvas?.ContentPosition ?? new NormalizedPoint(.5, .5),
            Scale = canvas?.ContentScale ?? 1
        };
        return state with { Position = state.Position with { } };
    }

    /// <summary>Samples the resolved camera motion pose on the original source clock for timeline editing.</summary>
    internal CameraMotionPose SampleCameraMotionAtSourceTime(double sourceTime)
    {
        ValidateSourceSampleTime(sourceTime);
        return EvaluateCameraMotion(sourceTime) ?? cameraBasePose
            ?? throw new InvalidOperationException("当前项目没有可采样的摄像头运动状态。");
    }

    private static void ValidateSourceSampleTime(double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceTime), "源时间必须是有限的非负值。");
    }

    private FrameOpeningState? EvaluateOpening(double outputTime, double sourceTime, OpeningElement element)
    {
        var opening = project.Settings.Opening?.ResolvePreset();
        if (opening is null || !opening.Enabled || outputTime >= opening.Duration || !opening.IncludedElements.Contains(element)) return null;
        var active = ActiveOpeningElements(opening, sourceTime);
        var index = active.IndexOf(element);
        if (index < 0) return null;
        var gaps = Math.Max(active.Count - 1, 0);
        var stagger = gaps == 0 ? opening.Stagger : Math.Min(opening.Stagger, Math.Max(opening.Duration - .18, 0) / gaps);
        var itemDuration = Math.Max(opening.Duration - stagger * gaps, .18);
        var linear = Math.Clamp((outputTime - stagger * index) / itemDuration, 0, 1);
        if (opening.ElementCurve is { } elementCurve && opening.Preset is OpeningPreset.Converge or OpeningPreset.SideSlide or OpeningPreset.Light3D)
            return OpeningMotionEvaluator.Sample(opening.Preset, element, linear, elementCurve);
        double Progress(double value) => opening.ElementCurve is { } curve
            ? OpeningMotionEvaluator.Progress(value, curve)
            : Ease(value, opening.Easing, opening.CustomCurve, project.Settings.Spring);
        var progress = Progress(linear);
        var start = OpeningStart(opening, element);
        var opacity = element == OpeningElement.Screen && opening.Preset is not (OpeningPreset.Converge or OpeningPreset.SideSlide or OpeningPreset.Light3D)
            ? opening.OpacityFrom + (1 - opening.OpacityFrom) * progress
            : Progress(Math.Clamp(linear * 1.65, 0, 1));
        return new FrameOpeningState(
            start.Scale + (1 - start.Scale) * progress,
            start.OffsetX * (1 - progress), start.OffsetY * (1 - progress),
            start.Rotation * (1 - progress), opacity,
            start.RotationX * Math.Pow(1 - linear, 1.5), start.RotationY * Math.Pow(1 - linear, 1.5),
            start.Perspective * Math.Pow(1 - linear, 1.5));
    }

    private List<OpeningElement> ActiveOpeningElements(OpeningSettings opening, double initialSourceTime)
    {
        var available = new HashSet<OpeningElement> { OpeningElement.Screen };
        if (project.Camera is { Opacity: > 0, Duration: > 0 } camera && TryCameraSourceTime(camera, initialSourceTime, out _))
            available.Add(OpeningElement.Camera);
        if (overlays.Any(x => x.Start <= initialSourceTime + .0001 && x.End > initialSourceTime + .0001)) available.Add(OpeningElement.Overlays);
        var ordered = opening.ElementOrder.Where(x => opening.IncludedElements.Contains(x) && available.Contains(x)).Distinct().ToList();
        ordered.AddRange(opening.IncludedElements.Where(x => available.Contains(x) && !ordered.Contains(x)));
        return ordered;
    }

    private static (double Scale, double OffsetX, double OffsetY, double Rotation, double RotationX, double RotationY, double Perspective) OpeningStart(OpeningSettings opening, OpeningElement element)
    {
        if (element == OpeningElement.Screen)
        {
            var rotationY = opening.Preset == OpeningPreset.Light3D ? -22 : 0;
            var rotationX = opening.Preset == OpeningPreset.Light3D ? 9 : 0;
            var perspective = opening.Preset == OpeningPreset.Light3D ? .72 : 0;
            return (opening.ScaleFrom, opening.OffsetX, opening.OffsetY, opening.RotationFrom, rotationX, rotationY, perspective);
        }
        return opening.Preset switch
        {
            OpeningPreset.Converge => element == OpeningElement.Camera ? (.58, 1, .72, 0, 0, 0, 0) : (.62, -1, -.70, 0, 0, 0, 0),
            OpeningPreset.SideSlide => element == OpeningElement.Camera ? (.88, 1, 0, 0, 0, 0, 0) : (.9, -1, 0, 0, 0, 0, 0),
            OpeningPreset.Light3D => element == OpeningElement.Camera ? (.72, 1, .16, 0, 0, 0, 0) : (.76, -1, -.12, 0, 0, 0, 0),
            _ => (opening.ScaleFrom, opening.OffsetX, opening.OffsetY, opening.RotationFrom, 0, 0, 0)
        };
    }

    /// <summary>The state the screen card returns to when no motion clip is active: the canvas content position and scale.</summary>
    private ScreenMotionState BaseScreenState()
    {
        var canvas = project.Settings.Canvas;
        var position = canvas?.ContentPosition ?? new NormalizedPoint(.5, .5);
        return new ScreenMotionState { Position = position.IsFinite ? position : new(.5, .5), Scale = canvas?.ContentScale ?? 1 };
    }

    private bool MotionTouchesNext(int index)
        => index + 1 < motionClips.Length && Math.Abs(motionClips[index + 1].Start - motionClips[index].End) <= 1d / 120;

    /// <summary>
    /// Mac ScreenMotionTrack.sampleLayout: a clip eases from the last completed target (touching clips chain directly) or from the base
    /// placement, holds a point-of-interest framing at its target and falls back to the base placement over its return window.
    /// Returns null state (base placement) when no clip influences the screen.
    /// </summary>
    private (ScreenMotionState? State, ScreenMotionBlend? Blend) EvaluateScreenMotion(double time)
    {
        if (motionClips.Length == 0) return (null, null);
        var index = UpperBound(motionStarts, time) - 1;
        return index < 0 ? (null, null) : SampleMotion(index, time);
    }

    private (ScreenMotionState? State, ScreenMotionBlend? Blend) SampleMotion(int index, double time)
    {
        var clip = motionClips[index];
        var baseState = BaseScreenState();
        if (time < clip.End)
        {
            var inherited = baseState; var inheritedFraming = ScreenMotionFraming.Placement;
            if (index > 0)
            {
                var previous = motionClips[index - 1];
                if (previous.End > clip.Start + 1e-4)
                {
                    // Overlapping clips (Windows allowed them; Mac validation does not): start from the pose the previous clip has at this moment.
                    var (overlapState, _) = SampleMotion(index - 1, clip.Start);
                    inherited = overlapState ?? baseState; inheritedFraming = overlapState is null ? ScreenMotionFraming.Placement : ScreenMotionFraming.Focus;
                }
                else if (!MotionTouchesNext(index - 1) && previous.ReturnDuration > 1e-4) { inherited = baseState; inheritedFraming = ScreenMotionFraming.Placement; }
                else { inherited = previous.Target; inheritedFraming = ScreenMotionFraming.Focus; }
            }
            var leadIn = Math.Min(clip.EnterDuration, clip.Duration);
            if (leadIn <= 1e-4) return Hold(clip.Target);
            var progress = Ease(Math.Min((time - clip.Start) / leadIn, 1), clip.Easing, clip.CustomCurve, project.Settings.Spring);
            return Blend(inherited, clip.Target, progress, inheritedFraming, ScreenMotionFraming.Focus,
                inheritedFraming == ScreenMotionFraming.Focus ? inherited.Position : null, clip.Target.Position);
        }
        // A sub-frame gap still counts as a joined keyframe: hold the outgoing plane until the next clip starts.
        if (MotionTouchesNext(index)) return Hold(clip.Target);
        if (clip.ReturnDuration > 1e-4)
        {
            if (time > clip.End + clip.ReturnDuration) return (null, null);
            var local = Math.Clamp((time - clip.End) / clip.ReturnDuration, 0, 1);
            var progress = Ease(local, clip.Easing, clip.CustomCurve, project.Settings.Spring);
            return Blend(clip.Target, baseState, progress, ScreenMotionFraming.Focus, ScreenMotionFraming.Placement, null, clip.Target.Position);
        }
        // No return window and nothing follows: the screen snaps back to its base placement after the clip.
        if (time <= clip.End) return Hold(clip.Target);
        return (null, null);

        (ScreenMotionState?, ScreenMotionBlend?) Hold(ScreenMotionState target)
            => (target,
                new ScreenMotionBlend(target, target, ScreenMotionFraming.Focus, ScreenMotionFraming.Focus, 0, target.Position));
    }

    private static (ScreenMotionState?, ScreenMotionBlend?) Blend(ScreenMotionState fromState, ScreenMotionState toState, double t,
        ScreenMotionFraming fromFraming, ScreenMotionFraming toFraming, NormalizedPoint? inheritedAnchor, NormalizedPoint anchor)
    {
        t = Math.Clamp(t, 0, 1);
        var state = Interpolate(fromState, toState, t);
        // A first entry from the flat base keeps the authored pivot from its first frame; a previously tilted target carries
        // its pivot into the next transition and moves it with the same eased progress as the plane.
        var pivot = inheritedAnchor is { } inherited
            ? new NormalizedPoint(inherited.X + (anchor.X - inherited.X) * t, inherited.Y + (anchor.Y - inherited.Y) * t)
            : anchor;
        return (state, new ScreenMotionBlend(fromState, toState, fromFraming, toFraming, t, pivot));
    }

    private static ScreenMotionState Interpolate(ScreenMotionState a, ScreenMotionState b, double t) => new()
    {
        Position = new(a.Position.X + (b.Position.X - a.Position.X) * t, a.Position.Y + (b.Position.Y - a.Position.Y) * t),
        Scale = a.Scale + (b.Scale - a.Scale) * t,
        RotationX = ScreenMotionLayout.MixAngle(a.RotationX, b.RotationX, t),
        RotationY = ScreenMotionLayout.MixAngle(a.RotationY, b.RotationY, t),
        RotationZ = ScreenMotionLayout.MixAngle(a.RotationZ, b.RotationZ, t),
        Perspective = a.Perspective + (b.Perspective - a.Perspective) * t
    };

    private FrameCameraScene? EvaluateCamera(double time, FrameOpeningState? opening, double screenZoomScale)
    {
        var camera = project.Camera;
        if (camera is null || !TryCameraSourceTime(camera, time, out var local)) return null;
        var pose = EvaluateCameraMotion(time) ?? cameraBasePose;
        if (pose is null) return null;
        var zoomProgress = Math.Clamp((screenZoomScale - 1) / .565, 0, 1);
        var linkedZoomScale = 1 + (Math.Clamp(camera.ScaleDuringZoom, .1, 2) - 1) * zoomProgress;
        var resolvedPose = pose.Value with { Size = pose.Value.Size * linkedZoomScale };
        var rect = resolvedPose.ToNormalizedRect(cameraCanvasAspect);
        if (opening is { } open)
        {
            var width = rect.Width * open.Scale; var height = rect.Height * open.Scale;
            rect = new(rect.X + rect.Width / 2 - width / 2 + open.OffsetX,
                rect.Y + rect.Height / 2 - height / 2 + open.OffsetY, width, height);
        }
        var cornerRadius = resolvedPose.ToNormalizedCornerRadius(cameraCanvasAspect) * (opening?.Scale ?? 1);
        var retainsLegacyShape = camera.Layout is null && cameraMotions.All(x => x.TargetLayout is null && x.TargetRoundness is null);
        var frameShape = retainsLegacyShape ? camera.Shape : CameraShape.RoundedRectangle;
        return new FrameCameraScene(camera.MediaPath, local, rect,
            cornerRadius, camera.BorderWidth * (opening?.Scale ?? 1), pose.Value.Opacity * (opening?.Opacity ?? 1), camera.Mirrored, camera.Volume, camera.Muted,
            frameShape, camera.Crop, camera.BorderColor, opening?.Rotation ?? 0, camera.ShadowStrength);
    }

    private bool TryCameraSourceTime(CameraTrack camera, double screenSourceTime, out double cameraSourceTime)
    {
        if (cameraSyncSegments.Length == 0)
        {
            if (screenSourceTime >= camera.TimelineStart && screenSourceTime < camera.TimelineStart + camera.Duration)
            { cameraSourceTime = camera.SourceStart + screenSourceTime - camera.TimelineStart; return true; }
            cameraSourceTime = 0; return false;
        }
        var low = 0; var high = cameraSyncSegments.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (cameraSyncSegments[mid].TimelineStart <= screenSourceTime) low = mid + 1; else high = mid;
        }
        var index = low - 1;
        if (index >= 0)
        {
            var segment = cameraSyncSegments[index];
            var offset = screenSourceTime - segment.TimelineStart;
            if (offset >= 0 && offset < segment.Duration) { cameraSourceTime = segment.SourceStart + offset * segment.SourceRate; return true; }
        }
        cameraSourceTime = 0; return false;
    }

    private CameraMotionPose? EvaluateCameraMotion(double time)
    {
        if (cameraMotions.Length == 0) return cameraBasePose;
        var index = UpperBound(cameraMotionStarts, time) - 1;
        if (index < 0) return cameraBasePose;
        var clip = cameraMotions[index];
        if (time > clip.End + clip.ReturnDuration) return cameraBasePose;
        var destination = index + 1 < cameraMotions.Length && cameraMotions[index + 1].Start <= clip.End + clip.ReturnDuration
            ? cameraMotionTargets[index + 1] : cameraBasePose;
        return EvaluateCameraMotionClip(index, time, cameraMotionFromStates[index], destination);
    }

    private CameraMotionPose? EvaluateCameraMotionClip(int index, double time, CameraMotionPose? from, CameraMotionPose? destination)
    {
        var clip = cameraMotions[index];
        var target = cameraMotionTargets[index];
        if (clip.EnterDuration > 0 && time < clip.Start + clip.EnterDuration)
            return CameraMotionEvaluator.Interpolate(from, target, Ease(Math.Clamp((time - clip.Start) / clip.EnterDuration, 0, 1), clip.Easing, clip.CustomCurve, project.Settings.Spring));
        if (time <= clip.End) return target;
        if (clip.ReturnDuration <= 0) return destination ?? target;
        var progress = Ease(Math.Clamp((time - clip.End) / clip.ReturnDuration, 0, 1), clip.Easing, clip.CustomCurve, project.Settings.Spring);
        return CameraMotionEvaluator.Interpolate(target, destination, progress);
    }

    private IReadOnlyList<FrameOverlayScene> EvaluateOverlays(double time, FrameOpeningState? opening, double initialSourceTime, out FrameOverlayBackdrop backdrop)
    {
        var result = new List<FrameOverlayScene>();
        var suppressInitialEntry = project.Settings.Opening is { Enabled: true } openingSettings
            && openingSettings.IncludedElements.Contains(OpeningElement.Overlays);
        var trackSample = overlayTrack.Evaluate(time, suppressInitialEntry, initialSourceTime);
        backdrop = trackSample.Backdrop;
        foreach (var sample in trackSample.Overlays)
        {
            var item = sample.Clip;
            var enter = item.EnterDuration <= 0 ? 1 : Math.Clamp((time - item.Start) / item.EnterDuration, 0, 1);
            var exit = item.ExitDuration <= 0 ? 1 : Math.Clamp((item.End - time) / item.ExitDuration, 0, 1);
            if (suppressInitialEntry && item.Start <= initialSourceTime + .0001) enter = 1;
            var rect = item.Rect; var opacity = item.Opacity;
            if (item.AnimationCurve is not null)
            {
                rect = OverlayMotionEvaluator.Transform(item.Rect, sample.Motion, sample.Progress);
                opacity = item.Opacity * sample.Visibility;
            }
            else
            {
                ApplyOverlayMotion(item.EnterMotion, 1 - enter, ref rect, ref opacity);
                ApplyOverlayMotion(item.ExitMotion, 1 - exit, ref rect, ref opacity);
            }
            var rotation = item.Rotation;
            if (opening is { } open)
            {
                var width = rect.Width * open.Scale; var height = rect.Height * open.Scale;
                rect = new(rect.X + rect.Width / 2 - width / 2 + open.OffsetX,
                    rect.Y + rect.Height / 2 - height / 2 + open.OffsetY, width, height);
                opacity *= open.Opacity; rotation += open.Rotation;
            }
            var scale = opening?.Scale ?? 1;
            result.Add(new FrameOverlayScene(item.Id, item.MediaPath, rect, rotation, Math.Clamp(opacity, 0, 1), item.Layer, item.CornerRadius * scale, item.Blur,
                item.BorderWidth * scale, item.BorderColor, item.ShadowOpacity, item.ShadowRadius * scale, item.ShadowOffsetX * scale, item.ShadowOffsetY * scale));
        }
        return result;
    }

    private static void ApplyOverlayMotion(OverlayMotion motion, double amount, ref NormalizedRect rect, ref double opacity)
    {
        if (amount <= 0) return;
        switch (motion)
        {
            case OverlayMotion.Fade: opacity *= 1 - amount; break;
            case OverlayMotion.Scale:
                var scale = 1 - .35 * amount; var dx = rect.Width * (1 - scale) / 2; var dy = rect.Height * (1 - scale) / 2;
                rect = new(rect.X + dx, rect.Y + dy, rect.Width * scale, rect.Height * scale); break;
            case OverlayMotion.SlideUp: rect = rect with { Y = rect.Y + rect.Height * amount }; break;
            case OverlayMotion.SlideDown: rect = rect with { Y = rect.Y - rect.Height * amount }; break;
            case OverlayMotion.SlideLeft: rect = rect with { X = rect.X + rect.Width * amount }; break;
            case OverlayMotion.SlideRight: rect = rect with { X = rect.X - rect.Width * amount }; break;
            case OverlayMotion.SlideTopLeft: rect = rect with { X = rect.X + rect.Width * amount, Y = rect.Y + rect.Height * amount }; break;
            case OverlayMotion.SlideTopRight: rect = rect with { X = rect.X - rect.Width * amount, Y = rect.Y + rect.Height * amount }; break;
            case OverlayMotion.SlideBottomLeft: rect = rect with { X = rect.X + rect.Width * amount, Y = rect.Y - rect.Height * amount }; break;
            case OverlayMotion.SlideBottomRight: rect = rect with { X = rect.X - rect.Width * amount, Y = rect.Y - rect.Height * amount }; break;
        }
    }

    private (FocusEffect? Effect, double Progress, NormalizedPoint Anchor, bool ScreenSelected) EvaluateFocus(double time, ZoomClip? zoomClip, NormalizedPoint zoomFocus)
    {
        var index = UpperBound(motionStarts, time) - 1;
        if (index >= 0)
        {
            var screenClip = motionClips[index];
            if (screenClip.Focus is { } screenEffect && time >= screenClip.Start && time <= screenClip.End + screenClip.ReturnDuration)
            {
                var progress = TransitionProgress(time, screenClip.Start, screenClip.End, screenClip.EnterDuration,
                    screenClip.ReturnDuration, screenClip.Easing, screenClip.CustomCurve, holdsAtEnd: screenClip.ReturnDuration == 0);
                if (progress > 0)
                    return (screenEffect, progress, new(canvasCrop.X + screenClip.Target.Position.X * canvasCrop.Width,
                        canvasCrop.Y + screenClip.Target.Position.Y * canvasCrop.Height), true);
                // The original evaluator gives an overlapping screen focus priority
                // even while its own transition has zero opacity.
                return (null, 0, default, true);
            }
        }

        if (zoomClip?.FocusEffect is not { } zoomEffect) return (null, 0, default, false);
        var zoomProgress = TransitionProgress(time, zoomClip.Start, zoomClip.End, zoomClip.EnterDuration,
            zoomClip.ExitDuration, zoomClip.Easing, zoomClip.CustomCurve, holdsAtEnd: false);
        if (zoomProgress <= 0) return (null, 0, default, false);
        return (zoomEffect, zoomProgress, new(canvasCrop.X + zoomFocus.X * canvasCrop.Width,
            canvasCrop.Y + zoomFocus.Y * canvasCrop.Height), false);
    }

    private double TransitionProgress(double time, double start, double end, double enterDuration, double returnDuration,
        ZoomEasing easing, ZoomBezierCurve curve, bool holdsAtEnd)
    {
        if (time < start) return 0;
        enterDuration = Math.Min(enterDuration, Math.Max(end - start, 0));
        if (enterDuration > 0 && time < start + enterDuration)
            return Ease(Math.Clamp((time - start) / enterDuration, 0, 1), easing, curve, project.Settings.Spring);
        if (time < end) return 1;
        if (returnDuration > 0 && time < end + returnDuration)
            return 1 - Ease(Math.Clamp((time - end) / returnDuration, 0, 1), easing, curve, project.Settings.Spring);
        return holdsAtEnd && time == end ? 1 : 0;
    }

    private IReadOnlyList<FrameBlurScene> EvaluateBlurRegions(double time)
    {
        var result = new List<FrameBlurScene>();
        foreach (var clip in blurClips)
        {
            if (time < clip.Start || time >= clip.End) continue;
            double Curve(double t) => clip.TransitionStyle switch
            {
                RegionTransitionStyle.None => 1, RegionTransitionStyle.Linear => t,
                RegionTransitionStyle.Smooth => t * t * t * (10 + t * (-15 + 6 * t)),
                _ => Ease(t, ZoomEasing.Cubic)
            };
            var enter = clip.EnterDuration <= 0 ? 1 : Curve(Math.Clamp((time - clip.Start) / clip.EnterDuration, 0, 1));
            var exit = clip.ExitDuration <= 0 ? 1 : Curve(Math.Clamp((clip.End - time) / clip.ExitDuration, 0, 1));
            result.Add(new FrameBlurScene(clip.Rect, clip.Strength, clip.Kind, clip.CornerRadius, Math.Min(enter, exit), clip.SpotlightDimming, clip.SourceSpace));
        }
        return result;
    }

    /// <summary>Playback rate of the retained segment that contains the source time (nearest segment when the time was cut away).</summary>
    private double RateAtSource(double sourceTime)
    {
        var best = 1d; var bestDistance = double.PositiveInfinity;
        foreach (var s in segments)
        {
            if (sourceTime >= s.SourceStart && sourceTime <= s.SourceEnd) return s.PlaybackRate;
            var distance = sourceTime < s.SourceStart ? s.SourceStart - sourceTime : sourceTime - s.SourceEnd;
            if (distance < bestDistance) { bestDistance = distance; best = s.PlaybackRate; }
        }
        return best;
    }
    /// <summary>Rate in force just after a source time; converts a transition running forward from it (enter, exit, return).</summary>
    private double RateAfter(double sourceTime) => RateAtSource(sourceTime + 1e-6);
    /// <summary>Rate in force just before a source time; converts a transition that ends at it (overlay and region exits).</summary>
    private double RateBefore(double sourceTime) => RateAtSource(sourceTime - 1e-6);

    /// <summary>
    /// Mac ProjectPointerTrack.cutBridgePosition: for 0.28 s of output time after a cut the pointer glides from where the previous
    /// retained segment ended to where this one will be, instead of teleporting. A click inside the window shortens it.
    /// </summary>
    private NormalizedPoint? BridgeCut(double outputTime, double sourceTime, NormalizedPoint? position)
    {
        if (position is null || segments.Length < 2) return position;
        var index = Array.BinarySearch(outputEnds, outputTime);
        if (index < 0) index = ~index; else if (index < outputEnds.Length - 1) index++;
        if (index <= 0 || index >= segments.Length || !segmentFollowsCut[index]) return position;
        var start = outputEnds[index - 1];
        var duration = Math.Min(Math.Min(.28, segmentFirstClickOutput[index]), segments[index].OutputDuration);
        if (!(duration > 1e-4) || outputTime < start || outputTime >= start + duration) return position;
        var previous = segments[index - 1];
        double ToPointer(double source) => (source - project.MouseClock.OffsetSeconds) / project.MouseClock.Rate;
        var bridgeFrom = pointerTrack.Sample(ToPointer(Math.Max(previous.SourceStart, previous.SourceEnd - 1e-4))).Position;
        var bridgeTo = pointerTrack.Sample(ToPointer(segments[index].SourceStart + duration * segments[index].PlaybackRate)).Position;
        if (bridgeFrom is not { } a || bridgeTo is not { } b) return position;
        var x = Math.Clamp((outputTime - start) / duration, 0, 1);
        var eased = x * x * x * (x * (x * 6 - 15) + 10);
        return new NormalizedPoint(a.X + (b.X - a.X) * eased, a.Y + (b.Y - a.Y) * eased);
    }

    private double MapOutputToSource(double outputTime)
    {
        var index = Array.BinarySearch(outputEnds, outputTime);
        if (index < 0) index = ~index;
        else if (index < outputEnds.Length - 1) index++;
        if (index >= segments.Length) index = segments.Length - 1;
        var segment = segments[index]; var outputStart = index == 0 ? 0 : outputEnds[index - 1];
        return segment.SourceStart + Math.Clamp(outputTime - outputStart, 0, segment.OutputDuration) * segment.PlaybackRate;
    }
    private static int UpperBound(double[] values, double time)
    { var low = 0; var high = values.Length; while (low < high) { var mid = low + (high - low) / 2; if (values[mid] <= time) low = mid + 1; else high = mid; } return low; }
    private static double Ease(double x, ZoomEasing easing, ZoomBezierCurve customCurve = default, SpringAnimationSettings? spring = null)
    {
        x = Math.Clamp(x, 0, 1);
        return easing switch
        {
            ZoomEasing.Linear => x,
            ZoomEasing.Quintic => Bezier(x, ZoomBezierCurve.Quintic),
            ZoomEasing.Spring => Spring(x, spring ?? new SpringAnimationSettings()),
            ZoomEasing.Quadratic => Bezier(x, ZoomBezierCurve.Quadratic),
            ZoomEasing.Custom => Bezier(x, customCurve.IsValid ? customCurve : ZoomBezierCurve.Cubic),
            _ => Bezier(x, ZoomBezierCurve.Cubic)
        };
    }

    // These control points and the normalized spring are the defaults in
    // RecorderCore/ZoomAnimation.swift. Solving x(t)=time is essential: using
    // the Bezier parameter directly produces a visibly different timeline.
    private static double Bezier(double x, ZoomBezierCurve curve)
    {
        if (x is <= 0 or >= 1) return x;
        static double Coordinate(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
        }
        static double Derivative(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
        }

        var t = x;
        for (var i = 0; i < 7; i++)
        {
            var error = Coordinate(t, curve.X1, curve.X2) - x;
            if (Math.Abs(error) < .00001) return Coordinate(t, curve.Y1, curve.Y2);
            var derivative = Derivative(t, curve.X1, curve.X2);
            if (Math.Abs(derivative) < .00001) break;
            var next = t - error / derivative;
            if (next is < 0 or > 1) break;
            t = next;
        }

        var low = 0.0;
        var high = 1.0;
        t = x;
        for (var i = 0; i < 18; i++)
        {
            var value = Coordinate(t, curve.X1, curve.X2);
            if (Math.Abs(value - x) < .00001) return Coordinate(t, curve.Y1, curve.Y2);
            if (value < x) low = t; else high = t;
            t = (low + high) / 2;
        }
        return Coordinate(t, curve.Y1, curve.Y2);
    }

    private static double Spring(double x, SpringAnimationSettings spring)
    {
        if (x is <= 0 or >= 1) return x;
        var mass = Math.Max(spring.Mass, .01); var stiffness = Math.Max(spring.Stiffness, .01); var damping = Math.Max(spring.Damping, .01); const double duration = .72;
        var omega0 = Math.Sqrt(stiffness / mass);
        var zeta = Math.Min(damping / (2 * Math.Sqrt(stiffness * mass)), .9999);
        var omegaD = omega0 * Math.Sqrt(Math.Max(1 - zeta * zeta, double.Epsilon));
        double Response(double seconds)
        {
            var envelope = Math.Exp(-zeta * omega0 * seconds);
            var oscillation = Math.Cos(omegaD * seconds) + zeta * omega0 / omegaD * Math.Sin(omegaD * seconds);
            return 1 - envelope * oscillation;
        }
        return Math.Clamp(Response(x * duration) / Response(duration), 0, 1);
    }
}
