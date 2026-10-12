namespace FrameTrail;

/// <summary>
/// Cached automatic camera follow, ported from DogSC Windows pointer.js at 3bdcf00
/// and checked against upstream/dogsc/Sources/RecorderCore/ProjectPointerTrack.swift.
/// Source clips use source-clock time, while camera focus is local to the canvas crop.
/// </summary>
internal sealed class PreparedAutoFollowTrack
{
    internal const int MaximumTotalSamples = 1_000_000;
    private const double NativeStep = 1d / 120;
    private const double SettlePositionTolerance = 1e-6;
    private const double SettleVelocityTolerance = 1e-6;
    private readonly SamplePoint[] samples;

    private PreparedAutoFollowTrack(SamplePoint[] samples) => this.samples = samples;
    internal int SampleCount => samples.Length;

    internal static PreparedAutoFollowTrack Build(
        ZoomClip clip,
        IReadOnlyList<PointerEvent> events,
        SpringAnimationSettings spring,
        NormalizedRect crop,
        int availableSamples,
        IReadOnlyList<(double Start, double End)>? cutGaps = null)
    {
        if (availableSamples < 2)
            throw new InvalidDataException("自动镜头跟随活动区间超过安全采样上限，无法构建项目预览。");

        var duration = Math.Max(clip.End - clip.Start, 0);
        // Keep one sample on either side of the clip window. The preceding point
        // determines raw interpolation at clip.Start; the following point preserves
        // the source pointer's linear interpolation up to clip.End.
        var clipEvents = EventsWithNeighbors(events, clip.Start, clip.End);
        var enterDuration = Math.Clamp(clip.EnterDuration, 0, duration);
        var followStart = Math.Min(clip.Start + enterDuration, clip.End);
        var entryClick = FirstClick(clipEvents, clip.Start, followStart);
        var entry = entryClick is not null ? ToCropLocal(entryClick.Location, crop)
            : RawPosition(clipEvents, clip.Start) is { } raw ? ToCropLocal(raw, crop) : clip.Focus;
        entry = entry.Clamp();

        // The original rest area is 56% of the visible half span: (1 - 2*0.22)/(2*scale).
        var restAreaHalfSpan = Math.Max(.28 / Math.Max(clip.Scale, 1), .01);
        var x = entry.X; var y = entry.Y; var targetX = x; var targetY = y;
        var velocityX = 0d; var velocityY = 0d;
        var mass = Math.Max(spring.Mass, .01);
        var stiffness = Math.Max(spring.Stiffness, .01);
        var damping = Math.Max(spring.Damping, .01);
        var naturalFrequency = Math.Sqrt(stiffness / mass);
        // Semi-implicit Euler remains at the original 120 Hz cadence. High damping or
        // stiffness gets stable internal substeps rather than a larger outer timestep.
        var largestRate = Math.Max(naturalFrequency, damping / mass);
        var substeps = Math.Max(1, (int)Math.Min(100_000, Math.Ceiling(NativeStep * largestRate / .2)));
        var substepDuration = NativeStep / substeps;

        var values = new List<SamplePoint>(Math.Min(availableSamples, 4096)) { new(clip.Start, entry) };
        var time = clip.Start;
        // Source ranges removed by edits: the spring does not integrate across them (Mac follows the retained output clock),
        // so the camera keeps its position and velocity at the cut and chases the new pointer target instead of being
        // flung by motion that was deleted.
        var gaps = (cutGaps ?? []).Where(g => g.End > g.Start && g.End > clip.Start && g.Start < clip.End).OrderBy(g => g.Start).ToArray();
        var gapIndex = 0;
        while (time < clip.End)
        {
            while (gapIndex < gaps.Length && gaps[gapIndex].End <= time) gapIndex++;
            var nextTime = Math.Min(time + NativeStep, clip.End);
            if (gapIndex < gaps.Length && nextTime >= gaps[gapIndex].Start)
            {
                if (time >= gaps[gapIndex].Start - 1e-9)
                {
                    time = Math.Min(gaps[gapIndex].End, clip.End);
                    if (values.Count >= availableSamples)
                        throw new InvalidDataException("自动镜头跟随活动区间超过安全采样上限，无法构建项目预览。");
                    values.Add(new(time, new(x, y)));
                    gapIndex++;
                    continue;
                }
                nextTime = gaps[gapIndex].Start;
            }
            if (nextTime >= followStart && RawPosition(clipEvents, nextTime) is { } cursor)
            {
                var local = ToCropLocal(cursor, crop).Clamp();
                if (local.X < targetX - restAreaHalfSpan || local.X > targetX + restAreaHalfSpan) targetX = local.X;
                if (local.Y < targetY - restAreaHalfSpan || local.Y > targetY + restAreaHalfSpan) targetY = local.Y;
            }

            var frameDelta = nextTime - time;
            var remaining = frameDelta;
            while (remaining > 0)
            {
                var dt = Math.Min(remaining, substepDuration);
                velocityX += ((stiffness * (targetX - x) - damping * velocityX) / mass) * dt;
                velocityY += ((stiffness * (targetY - y) - damping * velocityY) / mass) * dt;
                x = Math.Clamp(x + velocityX * dt, 0, 1);
                y = Math.Clamp(y + velocityY * dt, 0, 1);
                remaining -= dt;
            }
            time = nextTime;

            if (values.Count >= availableSamples)
                throw new InvalidDataException("自动镜头跟随活动区间超过安全采样上限，无法构建项目预览。");
            values.Add(new(time, new(x, y)));

            // Once the final raw event has passed and the camera spring has settled,
            // the rest of a long silent tail is constant. Store its endpoint directly.
            if (Math.Abs(targetX - x) <= SettlePositionTolerance
                && Math.Abs(targetY - y) <= SettlePositionTolerance
                && Math.Abs(velocityX) <= SettleVelocityTolerance
                && Math.Abs(velocityY) <= SettleVelocityTolerance)
            {
                var staticUntil = StaticUntil(clipEvents, time, clip.End);
                if (staticUntil > time)
                {
                    time = staticUntil;
                    if (values.Count >= availableSamples)
                        throw new InvalidDataException("自动镜头跟随活动区间超过安全采样上限，无法构建项目预览。");
                    values.Add(new(time, new(x, y)));
                }
                // A settled spring only permits skipping the currently silent pointer
                // interval. Continue integrating after that point: later pointer input
                // can move the follow target again. Append the constant clip tail only
                // when the silent interval actually reaches clip.End.
                if (time >= clip.End) break;
            }
        }
        return new(values.ToArray());
    }

    internal NormalizedPoint Sample(double sourceTime)
    {
        if (samples.Length == 1 || sourceTime <= samples[0].Time) return samples[0].Position;
        if (sourceTime >= samples[^1].Time) return samples[^1].Position;
        var low = 0; var high = samples.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (samples[mid].Time <= sourceTime) low = mid + 1; else high = mid;
        }
        var before = samples[low - 1]; var after = samples[low];
        var span = after.Time - before.Time;
        var p = span > 0 ? Math.Clamp((sourceTime - before.Time) / span, 0, 1) : 0;
        return new(before.Position.X + (after.Position.X - before.Position.X) * p,
            before.Position.Y + (after.Position.Y - before.Position.Y) * p);
    }

    private static PointerEvent[] EventsWithNeighbors(IReadOnlyList<PointerEvent> events, double start, double end)
    {
        var low = LowerBound(events, start);
        if (low > 0) low--;
        var high = UpperBound(events, end);
        if (high < events.Count) high++;
        var result = new PointerEvent[Math.Max(0, high - low)];
        for (var i = 0; i < result.Length; i++) result[i] = events[low + i];
        return result;
    }

    private static double StaticUntil(IReadOnlyList<PointerEvent> events, double time, double end)
    {
        if (events.Count == 0) return end;
        var upper = UpperBound(events, time);
        if (upper == 0) return Math.Min(events[0].Time, end);
        if (upper >= events.Count) return end;
        var before = events[upper - 1];
        var after = events[upper];
        if (before.Location != after.Location) return time;
        var last = upper;
        while (last + 1 < events.Count && events[last + 1].Location == before.Location) last++;
        return Math.Min(events[last].Time, end);
    }

    private static PointerEvent? FirstClick(IReadOnlyList<PointerEvent> events, double start, double end)
    {
        var low = 0; var high = events.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (events[mid].Time < start) low = mid + 1; else high = mid;
        }
        for (var i = low; i < events.Count && events[i].Time <= end; i++)
            if (events[i].Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick) return events[i];
        return null;
    }

    private static NormalizedPoint ToCropLocal(NormalizedPoint source, NormalizedRect crop) => new(
        crop.Width > 1e-12 ? (source.X - crop.X) / crop.Width : .5,
        crop.Height > 1e-12 ? (source.Y - crop.Y) / crop.Height : .5);

    private static NormalizedPoint? RawPosition(IReadOnlyList<PointerEvent> events, double time)
    {
        if (events.Count == 0) return null;
        if (time <= events[0].Time) return events[0].Location;
        var low = 0; var high = events.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (events[mid].Time <= time) low = mid + 1; else high = mid;
        }
        if (low == 0) return null;
        var before = events[low - 1];
        if (low >= events.Count) return before.Location;
        var after = events[low]; var span = after.Time - before.Time;
        if (span <= 0) return before.Location;
        var p = Math.Clamp((time - before.Time) / span, 0, 1);
        return new(before.Location.X + (after.Location.X - before.Location.X) * p,
            before.Location.Y + (after.Location.Y - before.Location.Y) * p);
    }

    private static int LowerBound(IReadOnlyList<PointerEvent> events, double time)
    {
        var low = 0; var high = events.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (events[mid].Time < time) low = mid + 1; else high = mid;
        }
        return low;
    }

    private static int UpperBound(IReadOnlyList<PointerEvent> events, double time)
    {
        var low = 0; var high = events.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (events[mid].Time <= time) low = mid + 1; else high = mid;
        }
        return low;
    }

    private readonly record struct SamplePoint(double Time, NormalizedPoint Position);
}
