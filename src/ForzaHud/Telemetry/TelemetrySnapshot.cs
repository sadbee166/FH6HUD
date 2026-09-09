namespace ForzaHud.Telemetry;

/// <summary>
/// One decoded FH6 Data Out frame. This is what the game reported, nothing more:
/// no unit conversion, no derived grip state, no smoothing, no display formatting.
/// </summary>
/// <param name="IsRaceOn">1 while the player is actively driving, 0 in menus/pause/replay.</param>
/// <param name="TimestampMs">Game timestamp in milliseconds. Wraps to zero eventually.</param>
/// <param name="EngineMaxRpm">Engine peak RPM.</param>
/// <param name="EngineIdleRpm">Engine idle RPM.</param>
/// <param name="CurrentEngineRpm">Current engine RPM.</param>
/// <param name="AccelerationX">Car-local acceleration, X = right (m/s^2).</param>
/// <param name="AccelerationY">Car-local acceleration, Y = up (m/s^2).</param>
/// <param name="AccelerationZ">Car-local acceleration, Z = forward (m/s^2).</param>
/// <param name="AngularVelocityX">Angular velocity used as pitch rate (radians per second).</param>
/// <param name="AngularVelocityY">Angular velocity used as yaw rate (radians per second).</param>
/// <param name="Speed">Speed in meters per second.</param>
/// <param name="Power">Engine power in watts.</param>
/// <param name="Torque">Engine torque in newton-meters.</param>
/// <param name="Boost">Boost pressure in PSI above atmospheric. 0 for naturally aspirated cars.</param>
/// <param name="Throttle">Throttle input, 0..1.</param>
/// <param name="Brake">Brake input, 0..1.</param>
/// <param name="Clutch">Clutch input, 0..1.</param>
/// <param name="HandBrake">Handbrake input, 0..1.</param>
/// <param name="Gear">Raw gear byte: 0 = reverse, 1..10 = forward gears.</param>
/// <param name="Steer">Steering input in -1 (full left) .. +1 (full right). Device input, not wheel angle.</param>
/// <param name="TireSlipRatio">Per-tyre normalized slip ratio. 0 = full grip, |value| &gt; 1 = loss of grip.</param>
/// <param name="TireSlipAngle">Per-tyre normalized slip angle in radians.</param>
/// <param name="TireCombinedSlip">Per-tyre normalized combined slip.</param>
/// <param name="WheelRotationSpeed">Per-tyre wheel rotation speed in radians per second.</param>
/// <param name="CarOrdinal">Unique ID of the car make/model.</param>
/// <param name="CarPerformanceIndex">Car performance index.</param>
/// <param name="DrivetrainType">0 = FWD, 1 = RWD, 2 = AWD.</param>
/// <param name="NumCylinders">Engine cylinder count.</param>
/// <param name="ReceivedAt">Local monotonic timestamp at which the packet was received.</param>
public sealed record TelemetrySnapshot(
    bool IsRaceOn,
    uint TimestampMs,
    float EngineMaxRpm,
    float EngineIdleRpm,
    float CurrentEngineRpm,
    float AccelerationX,
    float AccelerationY,
    float AccelerationZ,
    float AngularVelocityX,
    float AngularVelocityY,
    float Speed,
    float Power,
    float Torque,
    float Boost,
    float Throttle,
    float Brake,
    float Clutch,
    float HandBrake,
    int Gear,
    float Steer,
    float Roll,
    float TireSlipRatioFrontLeft,
    float TireSlipRatioFrontRight,
    float TireSlipRatioRearLeft,
    float TireSlipRatioRearRight,
    float TireSlipAngleFrontLeft,
    float TireSlipAngleFrontRight,
    float TireSlipAngleRearLeft,
    float TireSlipAngleRearRight,
    float TireCombinedSlipFrontLeft,
    float TireCombinedSlipFrontRight,
    float TireCombinedSlipRearLeft,
    float TireCombinedSlipRearRight,
    float WheelRotationSpeedFrontLeft,
    float WheelRotationSpeedFrontRight,
    float WheelRotationSpeedRearLeft,
    float WheelRotationSpeedRearRight,
    int CarOrdinal,
    int CarPerformanceIndex,
    int DrivetrainType,
    int NumCylinders,
    TimeSpan ReceivedAt)
{
    /// <summary>Tyre slip ratios indexed FrontLeft, FrontRight, RearLeft, RearRight.</summary>
    public WheelValues<float> TireSlipRatio => new(
        TireSlipRatioFrontLeft, TireSlipRatioFrontRight, TireSlipRatioRearLeft, TireSlipRatioRearRight);

    /// <summary>Tyre slip angles indexed FrontLeft, FrontRight, RearLeft, RearRight.</summary>
    public WheelValues<float> TireSlipAngle => new(
        TireSlipAngleFrontLeft, TireSlipAngleFrontRight, TireSlipAngleRearLeft, TireSlipAngleRearRight);

    /// <summary>Tyre combined slip indexed FrontLeft, FrontRight, RearLeft, RearRight.</summary>
    public WheelValues<float> TireCombinedSlip => new(
        TireCombinedSlipFrontLeft, TireCombinedSlipFrontRight, TireCombinedSlipRearLeft, TireCombinedSlipRearRight);

    /// <summary>Wheel rotation speeds indexed FrontLeft, FrontRight, RearLeft, RearRight.</summary>
    public WheelValues<float> WheelRotationSpeed => new(
        WheelRotationSpeedFrontLeft, WheelRotationSpeedFrontRight,
        WheelRotationSpeedRearLeft, WheelRotationSpeedRearRight);

    /// <summary>Empty snapshot used before the first packet arrives.</summary>
    public static TelemetrySnapshot Empty { get; } = new(
        IsRaceOn: false, TimestampMs: 0,
        EngineMaxRpm: 0, EngineIdleRpm: 0, CurrentEngineRpm: 0,
        AccelerationX: 0, AccelerationY: 0, AccelerationZ: 0,
        AngularVelocityX: 0, AngularVelocityY: 0,
        Speed: 0, Power: 0, Torque: 0, Boost: 0,
        Throttle: 0, Brake: 0, Clutch: 0, HandBrake: 0,
        Gear: 0, Steer: 0, Roll: 0f,
        TireSlipRatioFrontLeft: 0, TireSlipRatioFrontRight: 0, TireSlipRatioRearLeft: 0, TireSlipRatioRearRight: 0,
        TireSlipAngleFrontLeft: 0, TireSlipAngleFrontRight: 0, TireSlipAngleRearLeft: 0, TireSlipAngleRearRight: 0,
        TireCombinedSlipFrontLeft: 0, TireCombinedSlipFrontRight: 0, TireCombinedSlipRearLeft: 0, TireCombinedSlipRearRight: 0,
        WheelRotationSpeedFrontLeft: 0, WheelRotationSpeedFrontRight: 0, WheelRotationSpeedRearLeft: 0, WheelRotationSpeedRearRight: 0,
        CarOrdinal: 0, CarPerformanceIndex: 0, DrivetrainType: 0, NumCylinders: 0,
        ReceivedAt: TimeSpan.Zero);
}

/// <summary>
/// Four per-wheel or per-tyre values, ordered FrontLeft, FrontRight, RearLeft, RearRight.
/// </summary>
public readonly record struct WheelValues<T>(T FrontLeft, T FrontRight, T RearLeft, T RearRight)
{
    /// <summary>Values in FL, FR, RL, RR order.</summary>
    public T[] ToArray() => [FrontLeft, FrontRight, RearLeft, RearRight];
}
