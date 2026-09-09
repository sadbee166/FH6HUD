namespace ForzaHud.Telemetry;

/// <summary>
/// Protocol definition for the Forza Horizon 6 "Data Out" UDP packet.
///
/// Source: official Forza Horizon 6 Data Out documentation.
/// The packet is fixed at 324 bytes, little-endian, no padding, no alignment.
/// It is the Horizon layout: 232-byte sled + 12-byte Horizon block + 79-byte dash tail + 1 trailing byte.
///
/// This type is the single place where byte offsets are defined. Nothing else in the
/// application is allowed to hard-code an offset into a Data Out packet.
/// </summary>
public static class ForzaPacketFormat
{
    /// <summary>Exact size, in bytes, of a complete FH6 Data Out packet.</summary>
    public const int PacketSize = 324;

    /// <summary>Smallest prefix that still contains the engine fields (sled header).</summary>
    public const int MinimumParseableSize = 20;

    /// <summary>
    /// Minimum packet length accepted by the parser. The HUD reads the complete fixed
    /// layout through the final dash-tail byte, so shorter datagrams are malformed.
    /// </summary>
    public const int MinimumUsableSize = PacketSize;

    public static class Sled
    {
        public const int IsRaceOn = 0;                        // S32
        public const int TimestampMs = 4;                     // U32

        public const int EngineMaxRpm = 8;                    // F32
        public const int EngineIdleRpm = 12;                  // F32
        public const int CurrentEngineRpm = 16;               // F32

        // Car local space: X = right, Y = up, Z = forward.
        public const int AccelerationX = 20;                  // F32
        public const int AccelerationY = 24;                  // F32
        public const int AccelerationZ = 28;                  // F32

        public const int VelocityX = 32;                      // F32
        public const int VelocityY = 36;                      // F32
        public const int VelocityZ = 40;                      // F32

        public const int AngularVelocityX = 44;                // F32
        public const int AngularVelocityY = 48;                // F32
        public const int AngularVelocityZ = 52;                // F32

        public const int Yaw = 56;                            // F32
        public const int Pitch = 60;                           // F32
        public const int Roll = 64;                            // F32

        public const int NormalizedSuspensionTravel = 68;      // F32 x4
        public const int TireSlipRatio = 84;                   // F32 x4
        public const int WheelRotationSpeed = 100;             // F32 x4
        public const int WheelOnRumbleStrip = 116;             // S32 x4
        public const int WheelInPuddle = 132;                  // S32 x4
        public const int SurfaceRumble = 148;                  // F32 x4
        public const int TireSlipAngle = 164;                  // F32 x4
        public const int TireCombinedSlip = 180;               // F32 x4
        public const int SuspensionTravelMeters = 196;         // F32 x4

        public const int CarOrdinal = 212;                     // S32
        public const int CarClass = 216;                       // S32
        public const int CarPerformanceIndex = 220;            // S32
        public const int DrivetrainType = 224;                 // S32
        public const int NumCylinders = 228;                   // S32
    }

    /// <summary>
    /// Fields present in Forza Horizon 6 but not in Forza Motorsport's Dash format.
    /// They are inserted after NumCylinders and before PositionX, which is why every
    /// dash-tail offset is shifted by 12 compared with the FM7 layout.
    /// </summary>
    public static class Horizon
    {
        public const int CarGroup = 232;                       // U32
        public const int SmashableVelDiff = 236;               // F32
        public const int SmashableMass = 240;                  // F32
    }

    public static class Dash
    {
        public const int PositionX = 244;                      // F32
        public const int PositionY = 248;                      // F32
        public const int PositionZ = 252;                      // F32

        public const int Speed = 256;                          // F32, meters per second
        public const int Power = 260;                          // F32, watts
        public const int Torque = 264;                         // F32, newton-meters

        public const int TireTemp = 268;                       // F32 x4
        public const int Boost = 284;                          // F32, PSI above atmospheric
        public const int Fuel = 288;                           // F32, 0..1
        public const int DistanceTraveled = 292;               // F32, meters

        public const int BestLap = 296;                        // F32, seconds
        public const int LastLap = 300;                        // F32, seconds
        public const int CurrentLap = 304;                     // F32, seconds
        public const int CurrentRaceTime = 308;                // F32, seconds

        public const int LapNumber = 312;                      // U16
        public const int RacePosition = 314;                   // U8
        public const int Accel = 315;                          // U8, 0..255
        public const int Brake = 316;                          // U8, 0..255
        public const int Clutch = 317;                         // U8, 0..255
        public const int HandBrake = 318;                      // U8, 0..255
        public const int Gear = 319;                           // U8
        public const int Steer = 320;                          // S8, -127..127
        public const int NormalizedDrivingLine = 321;          // S8
        public const int NormalizedAiBrakeDifference = 322;    // S8
    }
}
