namespace ForzaHud.Vehicle;

internal readonly record struct ShiftAccelerationPoint(float Speed, float Acceleration);

/// <summary>Measured acceleration envelope for one gear over vehicle speed.</summary>
internal sealed class ShiftAccelerationProfile
{
    public ShiftAccelerationProfile(
        float rpmPerSpeed,
        IReadOnlyList<ShiftAccelerationPoint> points)
    {
        RpmPerSpeed = rpmPerSpeed;
        Points = points;
    }

    public float RpmPerSpeed { get; }

    public IReadOnlyList<ShiftAccelerationPoint> Points { get; }

    public bool Covers(float startSpeed, float endSpeed) =>
        Points.Count >= 2
        && startSpeed >= Points[0].Speed
        && endSpeed <= Points[^1].Speed
        && endSpeed >= startSpeed;

    public float AccelerationAt(float speed)
    {
        if (speed <= Points[0].Speed)
        {
            return Points[0].Acceleration;
        }

        if (speed >= Points[^1].Speed)
        {
            return Points[^1].Acceleration;
        }

        for (var index = 1; index < Points.Count; index++)
        {
            var upper = Points[index];
            if (speed > upper.Speed)
            {
                continue;
            }

            var lower = Points[index - 1];
            var span = upper.Speed - lower.Speed;
            if (span <= 0f)
            {
                return upper.Acceleration;
            }

            var fraction = (speed - lower.Speed) / span;
            return lower.Acceleration
                + fraction * (upper.Acceleration - lower.Acceleration);
        }

        return Points[^1].Acceleration;
    }
}

internal static class ShiftAccelerationProfileBuilder
{
    public static Dictionary<int, ShiftAccelerationProfile> Build(
        IReadOnlyList<PowerCurveSample> samples,
        int minimumSamplesPerGear)
    {
        var result = new Dictionary<int, ShiftAccelerationProfile>();
        foreach (var group in samples
                     .Where(sample => sample.Gear is >= 1 and <= 10)
                     .Where(sample => float.IsFinite(sample.Rpm)
                                      && float.IsFinite(sample.SpeedMetersPerSecond)
                                      && float.IsFinite(sample.LongitudinalAcceleration)
                                      && sample.Rpm > 0f
                                      && sample.SpeedMetersPerSecond > 0f
                                      && sample.LongitudinalAcceleration > 0f)
                     .GroupBy(sample => sample.Gear))
        {
            if (group.Count() < Math.Max(2, minimumSamplesPerGear))
            {
                continue;
            }

            var points = group
                .GroupBy(sample => MathF.Round(sample.SpeedMetersPerSecond, 1))
                .Select(speedGroup => new ShiftAccelerationPoint(
                    speedGroup.Average(sample => sample.SpeedMetersPerSecond),
                    Median(speedGroup.Select(sample => sample.LongitudinalAcceleration))))
                .OrderBy(point => point.Speed)
                .ToArray();
            if (points.Length < 2)
            {
                continue;
            }

            var rpmPerSpeed = Median(group.Select(sample =>
                sample.CurrentRpmPerSpeed()));
            if (rpmPerSpeed > 0f && float.IsFinite(rpmPerSpeed))
            {
                result[group.Key] = new ShiftAccelerationProfile(rpmPerSpeed, points);
            }
        }

        return result;
    }

    private static float Median(IEnumerable<float> values)
    {
        var ordered = values
            .Where(float.IsFinite)
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0f;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2f
            : ordered[middle];
    }
}

internal static class ShiftPointOptimizer
{
    public static bool TryFindOptimalShiftRpm(
        float minimumShiftRpm,
        float maximumShiftRpm,
        float nextShiftRpm,
        float postShiftRpmRatio,
        ShiftAccelerationProfile currentGear,
        ShiftAccelerationProfile nextGear,
        float shiftDurationMilliseconds,
        float candidateStepRpm,
        int speedIntegrationSteps,
        out float shiftRpm)
    {
        shiftRpm = 0f;
        if (minimumShiftRpm <= 0f
            || maximumShiftRpm <= minimumShiftRpm
            || nextShiftRpm <= 0f
            || postShiftRpmRatio is <= 0f or >= 1f
            || candidateStepRpm <= 0f
            || currentGear.RpmPerSpeed <= 0f
            || nextGear.RpmPerSpeed <= 0f)
        {
            return false;
        }

        var startSpeed = minimumShiftRpm / currentGear.RpmPerSpeed;
        var endSpeed = nextShiftRpm / nextGear.RpmPerSpeed;
        var maximumShiftSpeed = Math.Min(
            maximumShiftRpm / currentGear.RpmPerSpeed,
            endSpeed);
        if (!currentGear.Covers(startSpeed, maximumShiftSpeed)
            || !nextGear.Covers(startSpeed, endSpeed)
            || endSpeed <= startSpeed)
        {
            return false;
        }

        var bestTime = float.PositiveInfinity;
        var bestShiftRpm = 0f;
        var firstCandidate = MathF.Ceiling(minimumShiftRpm / candidateStepRpm)
                             * candidateStepRpm;
        for (var candidate = firstCandidate;
             candidate < maximumShiftRpm;
             candidate += candidateStepRpm)
        {
            ConsiderCandidate(candidate);
        }
        ConsiderCandidate(maximumShiftRpm);

        shiftRpm = bestShiftRpm;
        return float.IsFinite(bestTime);

        void ConsiderCandidate(float candidate)
        {
            var postShiftRpm = candidate * postShiftRpmRatio;
            if (postShiftRpm >= nextShiftRpm)
            {
                return;
            }

            var shiftSpeed = candidate / currentGear.RpmPerSpeed;
            if (shiftSpeed <= startSpeed || shiftSpeed >= endSpeed)
            {
                return;
            }

            if (!TryIntegrateTime(
                    currentGear,
                    startSpeed,
                    shiftSpeed,
                    speedIntegrationSteps,
                    out var currentTime)
                || !TryIntegrateTime(
                    nextGear,
                    shiftSpeed,
                    endSpeed,
                    speedIntegrationSteps,
                    out var nextTime))
            {
                return;
            }

            var totalTime = currentTime
                            + nextTime
                            + Math.Max(0f, shiftDurationMilliseconds) / 1000f;
            if (totalTime < bestTime)
            {
                bestTime = totalTime;
                bestShiftRpm = candidate;
            }
        }
    }

    private static bool TryIntegrateTime(
        ShiftAccelerationProfile profile,
        float startSpeed,
        float endSpeed,
        int requestedSteps,
        out float seconds)
    {
        seconds = 0f;
        if (!profile.Covers(startSpeed, endSpeed))
        {
            return false;
        }

        var steps = Math.Max(1, requestedSteps);
        var speedStep = (endSpeed - startSpeed) / steps;
        for (var step = 0; step < steps; step++)
        {
            var lowerSpeed = startSpeed + step * speedStep;
            var upperSpeed = lowerSpeed + speedStep;
            var lowerAcceleration = profile.AccelerationAt(lowerSpeed);
            var upperAcceleration = profile.AccelerationAt(upperSpeed);
            if (lowerAcceleration <= 0f || upperAcceleration <= 0f)
            {
                return false;
            }

            seconds += speedStep * 2f / (lowerAcceleration + upperAcceleration);
        }

        return float.IsFinite(seconds);
    }
}

internal static class PowerCurveSampleExtensions
{
    public static float CurrentRpmPerSpeed(this PowerCurveSample sample) =>
        sample.SpeedMetersPerSecond > 0f
            ? sample.Rpm / sample.SpeedMetersPerSecond
            : 0f;
}
