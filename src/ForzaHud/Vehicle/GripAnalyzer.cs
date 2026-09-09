using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Interprets FH6 tyre telemetry as grip states.
///
/// FH6 reports normalized slip where 0 means full grip and |value| &gt; 1 means loss of
/// grip. Those are slip measurements, not a grip percentage, so this class reports discrete
/// states rather than inventing an "87% grip" number.
///
/// Raw telemetry stays available on <see cref="TelemetrySnapshot"/>; nothing here overwrites
/// what the game reported.
/// </summary>
public static class GripAnalyzer
{
    /// <summary>
    /// Classifies one wheel.
    /// </summary>
    /// <param name="slipRatio">Normalized slip ratio. Positive = wheel over-speeding the ground (power on), negative = wheel under-speeding (braking).</param>
    /// <param name="slipAngle">Normalized slip angle.</param>
    /// <param name="wheelSpeed">Wheel rotation speed in rad/s.</param>
    /// <param name="referenceWheelSpeed">Median wheel speed of the axle set, used to spot a single spinning wheel.</param>
    /// <param name="vehicleSpeed">Vehicle speed in m/s.</param>
    /// <param name="brake">Brake input, 0..1.</param>
    /// <param name="throttle">Throttle input, 0..1.</param>
    /// <param name="settings">Thresholds.</param>
    public static TireGripState Classify(
        float slipRatio,
        float slipAngle,
        float wheelSpeed,
        float referenceWheelSpeed,
        float vehicleSpeed,
        float brake,
        float throttle,
        GripSettings settings)
    {
        if (vehicleSpeed < settings.MinimumVehicleSpeed)
        {
            return TireGripState.WithinGrip;
        }

        var magnitude = Math.Abs(slipRatio);
        var wheelIsTurning = Math.Abs(wheelSpeed);
        var reference = Math.Abs(referenceWheelSpeed);

        // A locked wheel has stopped rotating while the car is still moving.
        if (brake > 0.05f
            && slipRatio <= -settings.SlipLossThreshold
            && wheelIsTurning < settings.LockedWheelSpeed)
        {
            return TireGripState.WheelLock;
        }

        // Wheelspin: the wheel is turning materially faster than the rest of the car.
        var spinsFasterThanReference = wheelIsTurning > reference * settings.WheelspinExcessRatio + settings.LockedWheelSpeed;
        if (throttle > 0.05f && slipRatio >= settings.SlipLossThreshold && spinsFasterThanReference)
        {
            return TireGripState.Wheelspin;
        }

        // Lateral slip dominates when the tyre is being asked to corner harder than it can.
        if (Math.Abs(slipAngle) >= settings.LateralSlipThreshold)
        {
            return TireGripState.LateralSlip;
        }

        if (magnitude >= settings.SlipLossThreshold)
        {
            return TireGripState.LongitudinalSlip;
        }

        if (magnitude >= settings.SlipApproachThreshold || Math.Abs(slipAngle) >= settings.SlipApproachThreshold)
        {
            return TireGripState.ApproachingLimit;
        }

        return TireGripState.WithinGrip;
    }

    /// <summary>Classifies all four tyres from one telemetry frame.</summary>
    public static WheelValues<TireGripState> Analyze(TelemetrySnapshot snapshot, GripSettings settings)
    {
        var wheels = snapshot.WheelRotationSpeed.ToArray();
        var median = Median(wheels);

        return new WheelValues<TireGripState>(
            Classify(snapshot.TireSlipRatioFrontLeft, snapshot.TireSlipAngleFrontLeft,
                     snapshot.WheelRotationSpeedFrontLeft, median, snapshot.Speed,
                     snapshot.Brake, snapshot.Throttle, settings),
            Classify(snapshot.TireSlipRatioFrontRight, snapshot.TireSlipAngleFrontRight,
                     snapshot.WheelRotationSpeedFrontRight, median, snapshot.Speed,
                     snapshot.Brake, snapshot.Throttle, settings),
            Classify(snapshot.TireSlipRatioRearLeft, snapshot.TireSlipAngleRearLeft,
                     snapshot.WheelRotationSpeedRearLeft, median, snapshot.Speed,
                     snapshot.Brake, snapshot.Throttle, settings),
            Classify(snapshot.TireSlipRatioRearRight, snapshot.TireSlipAngleRearRight,
                     snapshot.WheelRotationSpeedRearRight, median, snapshot.Speed,
                     snapshot.Brake, snapshot.Throttle, settings));
    }

    /// <summary>Orders states from calm to severe so a set of tyres can be summarized.</summary>
    public static TireGripState Worst(WheelValues<TireGripState> states)
    {
        var worst = TireGripState.WithinGrip;
        foreach (var state in states.ToArray())
        {
            if ((int)state > (int)worst)
            {
                worst = state;
            }
        }

        return worst;
    }

    /// <summary>True for states that mean grip has actually been lost.</summary>
    public static bool IsGripLoss(TireGripState state) =>
        state is TireGripState.LongitudinalSlip
            or TireGripState.LateralSlip
            or TireGripState.Wheelspin
            or TireGripState.WheelLock;

    private static float Median(float[] values)
    {
        Span<float> ordered = stackalloc float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            ordered[i] = Math.Abs(values[i]);
        }

        ordered.Sort();
        return ordered.Length % 2 == 0
            ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2f
            : ordered[ordered.Length / 2];
    }
}

/// <summary>
/// Grip condition of one tyre, ordered from calm to severe.
/// </summary>
public enum TireGripState
{
    /// <summary>Comfortably within grip.</summary>
    WithinGrip,

    /// <summary>Approaching the limit but still holding.</summary>
    ApproachingLimit,

    /// <summary>Slipping longitudinally: spinning or sliding without full traction loss.</summary>
    LongitudinalSlip,

    /// <summary>Slipping laterally.</summary>
    LateralSlip,

    /// <summary>Driven wheel spinning faster than the road.</summary>
    Wheelspin,

    /// <summary>Wheel has stopped rotating while the car is moving.</summary>
    WheelLock,
}
