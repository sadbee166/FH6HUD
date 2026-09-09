namespace ForzaHud.Vehicle;

/// <summary>One accepted raw observation retained while learning a power curve.</summary>
public readonly record struct PowerCurveSample(
    float Rpm,
    float Power,
    float Throttle,
    int Gear,
    float RpmRate,
    float WheelSlip,
    TimeSpan Timestamp)
{
    /// <summary>Vehicle speed in metres per second when the sample was captured.</summary>
    public float SpeedMetersPerSecond { get; init; }

    /// <summary>Speed-derived longitudinal acceleration in metres per second squared.</summary>
    public float LongitudinalAcceleration { get; init; }

    /// <summary>Drivetrain used to select the recorded tire-slip threshold.</summary>
    public int DrivetrainType { get; init; } = 2;
}

/// <summary>Confidence assigned to one RPM bin after raw-sample cleaning.</summary>
public enum PowerCurveBinStatus
{
    Unlearned,
    Provisional,
    Reliable,
}

/// <summary>Learning diagnostics for one populated RPM bin.</summary>
public sealed record PowerCurveBinConfidence(
    float Rpm,
    int ObservationCount,
    int IndependentPassCount,
    float SpreadFraction,
    int RejectedObservationCount,
    PowerCurveBinStatus Status)
{
    /// <summary>Time span covered by the retained observations in this bin.</summary>
    public TimeSpan ObservationAge { get; init; }
}

internal sealed record PowerCurveCleaningResult(
    IReadOnlyList<PowerCurveSample> Samples,
    IReadOnlyList<PowerCurvePoint> Curve,
    IReadOnlyList<PowerCurveBinConfidence> BinConfidence,
    IReadOnlyDictionary<int, int> RejectedByBin,
    int RejectedSampleCount);
