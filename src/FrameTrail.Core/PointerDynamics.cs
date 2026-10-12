namespace FrameTrail;

/// <summary>Motion and active-click values for a cursor at one source timestamp.</summary>
public readonly record struct PointerDynamicsSample(
    NormalizedPoint? Position,
    double RotationRadians,
    double? ClickProgress,
    PointerEventKind? ActiveClickKind);

/// <summary>
/// Evaluates pointer tilt and click timing from a prepared position track.
/// The position track must use the same smoothing mode passed to Sample.
/// </summary>
public sealed class PreparedPointerDynamics
{
    private readonly PreparedPointerTrack _positionTrack;
    private readonly PointerEvent[] _events;
    private readonly PointerEvent[] _clicks;

    public PreparedPointerDynamics(PreparedPointerTrack positionTrack, IEnumerable<PointerEvent> events)
    {
        ArgumentNullException.ThrowIfNull(positionTrack);
        ArgumentNullException.ThrowIfNull(events);
        _positionTrack = positionTrack;
        _events = events.Where(x => double.IsFinite(x.Time) && x.Time >= 0 && x.Location.IsFinite)
            .OrderBy(x => x.Time).ToArray();
        _clicks = _events.Where(x => x.Kind is PointerEventKind.LeftClick or PointerEventKind.RightClick).ToArray();
    }

    public PointerDynamicsSample Sample(double sourceTime, PointerSmoothing smoothing, double strength, double duration)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0 || !Enum.IsDefined(smoothing))
            return new(null, 0, null, null);
        var position = _positionTrack.Sample(sourceTime).Position;
        var rotation = SampleRotation(sourceTime, smoothing, strength);
        var clickIndex = UpperBound(_clicks, sourceTime) - 1;
        if (clickIndex < 0 || !double.IsFinite(duration) || duration <= 0)
            return new(position, rotation, null, null);

        var click = _clicks[clickIndex];
        var elapsed = sourceTime - click.Time;
        if (elapsed < 0 || elapsed > duration)
            return new(position, rotation, null, null);
        return new(position, rotation, Math.Clamp(elapsed / duration, 0, 1), click.Kind);
    }

    private double SampleRotation(double time, PointerSmoothing smoothing, double strength)
    {
        if (!double.IsFinite(strength) || strength <= 0 || _events.Length == 0) return 0;
        var boundedStrength = Math.Clamp(strength, 2, 4);
        var (window, maximumDegrees, previousWeight, responseSpan) = smoothing switch
        {
            PointerSmoothing.Smooth => (.08, 9d, .38, .18),
            PointerSmoothing.Medium => (.065, 8d, .24, .22),
            PointerSmoothing.Rapid or PointerSmoothing.None => (.05, 7d, .12, .28),
            _ => throw new ArgumentOutOfRangeException(nameof(smoothing))
        };
        var current = _positionTrack.Sample(time).Position;
        var previous = _positionTrack.Sample(time - window).Position;
        var older = _positionTrack.Sample(time - window * 2).Position;
        if (current is not { } p0 || previous is not { } p1 || older is not { } p2) return 0;

        var recentX = (p0.X - p1.X) / window;
        var recentY = (p0.Y - p1.Y) / window;
        var priorX = (p1.X - p2.X) / window;
        var priorY = (p1.Y - p2.Y) / window;
        var velocityX = recentX * (1 - previousWeight) + priorX * previousWeight;
        var velocityY = recentY * (1 - previousWeight) + priorY * previousWeight;
        var speed = Math.Sqrt(velocityX * velocityX + velocityY * velocityY);
        var threshold = .006 / boundedStrength;
        if (!double.IsFinite(speed) || speed <= threshold) return 0;

        var torque = Math.Clamp((velocityX - velocityY) / Math.Max(speed, 1e-6) / Math.Sqrt(2), -1, 1);
        var response = Math.Clamp((speed - threshold) / (responseSpan / boundedStrength), 0, 1);
        response = response * response * (3 - 2 * response);
        var lastEventIndex = UpperBound(_events, time) - 1;
        var idleElapsed = lastEventIndex < 0 ? double.PositiveInfinity : Math.Max(time - _events[lastEventIndex].Time, 0);
        var idleDecay = Math.Clamp(1 - idleElapsed / .18, 0, 1);
        var amplitude = .35 + .65 * boundedStrength;
        var degrees = -maximumDegrees * amplitude * response * torque * idleDecay;
        return degrees * Math.PI / 180;
    }

    private static int UpperBound(PointerEvent[] values, double time)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (values[mid].Time <= time) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
