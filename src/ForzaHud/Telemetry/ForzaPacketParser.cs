using System.Buffers.Binary;
using static ForzaHud.Telemetry.ForzaPacketFormat.Dash;
using static ForzaHud.Telemetry.ForzaPacketFormat.Sled;

namespace ForzaHud.Telemetry;

/// <summary>
/// Converts a raw FH6 Data Out packet into a <see cref="TelemetrySnapshot"/>.
///
/// Parsing is a pure function of the byte buffer: no state, no allocation beyond the
/// snapshot, and no exceptions for ordinary malformed input. A bad packet yields
/// <see langword="false"/> so the render loop can never be crashed by the network.
/// </summary>
public static class ForzaPacketParser
{
    /// <summary>
    /// Attempts to decode <paramref name="packet"/>.
    /// </summary>
    /// <returns><see langword="false"/> if the buffer is too short to contain a usable packet.</returns>
    public static bool TryParse(ReadOnlySpan<byte> packet, TimeSpan receivedAt, out TelemetrySnapshot snapshot)
    {
        snapshot = TelemetrySnapshot.Empty;

        if (packet.Length < ForzaPacketFormat.MinimumUsableSize)
        {
            return false;
        }

        snapshot = new TelemetrySnapshot(
            IsRaceOn: ReadInt32(packet, IsRaceOn) == 1,
            TimestampMs: ReadUInt32(packet, TimestampMs),
            EngineMaxRpm: ReadSingle(packet, EngineMaxRpm),
            EngineIdleRpm: ReadSingle(packet, EngineIdleRpm),
            CurrentEngineRpm: ReadSingle(packet, CurrentEngineRpm),
            AccelerationX: ReadSingle(packet, AccelerationX),
            AccelerationY: ReadSingle(packet, AccelerationY),
            AccelerationZ: ReadSingle(packet, AccelerationZ),
            AngularVelocityX: ReadSingle(packet, AngularVelocityX),
            AngularVelocityY: ReadSingle(packet, AngularVelocityY),
            Speed: ReadSingle(packet, Speed),
            Power: ReadSingle(packet, Power),
            Torque: ReadSingle(packet, Torque),
            Boost: ReadSingle(packet, Boost),
            Throttle: packet[Accel] / 255f,
            Brake: packet[Brake] / 255f,
            Clutch: packet[Clutch] / 255f,
            HandBrake: packet[HandBrake] / 255f,
            Gear: packet[Gear],
            Steer: NormalizeSteering((sbyte)packet[Steer]),
            Roll: ReadSingle(packet, Roll),
            TireSlipRatioFrontLeft: ReadSingle(packet, TireSlipRatio + 0),
            TireSlipRatioFrontRight: ReadSingle(packet, TireSlipRatio + 4),
            TireSlipRatioRearLeft: ReadSingle(packet, TireSlipRatio + 8),
            TireSlipRatioRearRight: ReadSingle(packet, TireSlipRatio + 12),
            TireSlipAngleFrontLeft: ReadSingle(packet, TireSlipAngle + 0),
            TireSlipAngleFrontRight: ReadSingle(packet, TireSlipAngle + 4),
            TireSlipAngleRearLeft: ReadSingle(packet, TireSlipAngle + 8),
            TireSlipAngleRearRight: ReadSingle(packet, TireSlipAngle + 12),
            TireCombinedSlipFrontLeft: ReadSingle(packet, TireCombinedSlip + 0),
            TireCombinedSlipFrontRight: ReadSingle(packet, TireCombinedSlip + 4),
            TireCombinedSlipRearLeft: ReadSingle(packet, TireCombinedSlip + 8),
            TireCombinedSlipRearRight: ReadSingle(packet, TireCombinedSlip + 12),
            WheelRotationSpeedFrontLeft: ReadSingle(packet, WheelRotationSpeed + 0),
            WheelRotationSpeedFrontRight: ReadSingle(packet, WheelRotationSpeed + 4),
            WheelRotationSpeedRearLeft: ReadSingle(packet, WheelRotationSpeed + 8),
            WheelRotationSpeedRearRight: ReadSingle(packet, WheelRotationSpeed + 12),
            CarOrdinal: ReadInt32(packet, CarOrdinal),
            CarPerformanceIndex: ReadInt32(packet, CarPerformanceIndex),
            DrivetrainType: ReadInt32(packet, DrivetrainType),
            NumCylinders: ReadInt32(packet, NumCylinders),
            ReceivedAt: receivedAt);

        return true;
    }

    /// <summary>
    /// Maps the raw steering byte to -1 (full left) .. +1 (full right).
    /// The documented range is -127..127; -128 is clamped so the mapping stays symmetric.
    /// </summary>
    public static float NormalizeSteering(sbyte rawSteer) => Math.Clamp(rawSteer / 127f, -1f, 1f);

    private static float ReadSingle(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(packet.Slice(offset, 4));

    private static int ReadInt32(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, 4));

    private static uint ReadUInt32(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, 4));
}
