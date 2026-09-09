using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Calculated vehicle state: everything the HUD draws, in display-ready form.
///
/// Every value here is derived, never raw telemetry. Anything the game did not report and
/// we cannot justify stays absent rather than being invented.
/// </summary>
public sealed class DerivedState
{
    /// <summary>False before the first packet, and while the game reports the player is not driving.</summary>
    public bool IsDriving { get; set; }

    /// <summary>True when the current vehicle has zero cylinders and is classified as an EV.</summary>
    public bool IsElectric { get; set; }

    /// <summary>Monotonic timestamp of the telemetry frame this state was derived from.</summary>
    public TimeSpan Timestamp { get; set; }

    /// <summary>Speed in the selected display unit.</summary>
    public float DisplaySpeed { get; set; }

    /// <summary>Speed in metres per second, kept for physics-adjacent calculations.</summary>
    public float SpeedMetersPerSecond { get; set; }

    /// <summary>Unit label for <see cref="DisplaySpeed"/>, e.g. "KM/H".</summary>
    public string SpeedUnitLabel { get; set; } = "KM/H";

    /// <summary>Current engine RPM.</summary>
    public float Rpm { get; set; }

    /// <summary>RPM as a fraction of <see cref="RpmRange.MaxRpm"/>.</summary>
    public float RpmFraction { get; set; }

    /// <summary>RPM normalized between idle and maximum, which is what a rev indicator should track.</summary>
    public float RpmNormalized { get; set; }

    public RpmRange RpmRange { get; set; }

    /// <summary>Raw gear byte from FH6: 0 = reverse, 1..10 = forward.</summary>
    public int Gear { get; set; }

    /// <summary>Display form of <see cref="Gear"/>: "R", "1".."10", or "-" when not driving.</summary>
    public string GearLabel { get; set; } = "-";

    /// <summary>Boost pressure in PSI above atmospheric.</summary>
    public float BoostPsi { get; set; }

    /// <summary>
    /// True once boost outside the deadband has been seen for the current car. Used to keep
    /// the boost element hidden on naturally aspirated vehicles instead of showing a dead gauge.
    /// </summary>
    public bool HasBoost { get; set; }

    /// <summary>Throttle input, 0..1.</summary>
    public float Throttle { get; set; }

    /// <summary>Brake input, 0..1.</summary>
    public float Brake { get; set; }

    /// <summary>Steering input, -1 (full left) .. +1 (full right). Device input, not wheel angle.</summary>
    public float Steer { get; set; }

    /// <summary>Car roll angle in degrees. Positive is right side down.</summary>
    public float RollDegrees { get; set; }

    /// <summary>Lateral acceleration in G. Positive is to the car's right, subject to configuration.</summary>
    public float LateralG { get; set; }

    /// <summary>Longitudinal acceleration in G. Positive is acceleration, subject to configuration.</summary>
    public float LongitudinalG { get; set; }

    /// <summary>Vertical acceleration in G. Positive is upwards, subject to configuration.</summary>
    public float VerticalG { get; set; }

    /// <summary>Angular acceleration derived from AngularVelocityX, in radians per second squared.</summary>
    public float PitchAngularAcceleration { get; set; }

    /// <summary>Angular acceleration derived from AngularVelocityY, in radians per second squared.</summary>
    public float YawAngularAcceleration { get; set; }

    /// <summary>Learned powerband for the current car.</summary>
    public PowerbandState Powerband { get; set; } = PowerbandState.Empty;

    /// <summary>Whether inferred traction-control intervention is currently active.</summary>
    public bool TractionControlActive { get; set; }

    /// <summary>Wheel-speed-only TCS evidence before fusion with the other detectors.</summary>
    public WheelSpeedTcsEvidence WheelSpeedTcsEvidence { get; set; } = WheelSpeedTcsEvidence.Empty;

    /// <summary>Grip classification for each tyre.</summary>
    public WheelValues<TireGripState> TireGrip { get; set; }

    /// <summary>The most severe state across all four tyres, used for at-a-glance emphasis.</summary>
    public TireGripState WorstTireGrip { get; set; } = TireGripState.WithinGrip;

    /// <summary>Whether any tyre has lost grip.</summary>
    public bool HasGripLoss { get; set; }
}

/// <summary>Engine speed limits reported by FH6.</summary>
public readonly record struct RpmRange(float IdleRpm, float MaxRpm)
{
    public bool IsValid => MaxRpm > IdleRpm && MaxRpm > 0;
}
