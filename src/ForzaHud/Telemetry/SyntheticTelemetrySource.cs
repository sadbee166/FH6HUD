using System.Buffers.Binary;
using static ForzaHud.Telemetry.ForzaPacketFormat.Dash;
using static ForzaHud.Telemetry.ForzaPacketFormat.Sled;

namespace ForzaHud.Telemetry;

/// <summary>
/// Generates scripted FH6 Data Out packets.
///
/// Exists so HUD behaviour can be developed and verified without launching FH6 and
/// driving a car. It writes through the same offsets the parser reads, which means it
/// doubles as a round-trip check of the protocol definition.
/// </summary>
public sealed class SyntheticTelemetrySource : ITelemetrySource
{
    private static readonly float[] GearRatios = [3.40f, 2.15f, 1.55f, 1.18f, 0.96f, 0.80f];
    private const float FinalDrive = 3.70f;
    private const float WheelCircumferenceMeters = 2.02f;
    private const float MaxRpm = 7600f;
    private const float IdleRpm = 900f;
    private const float UpshiftRpm = 6900f;
    private const float DownshiftRpm = 3200f;

    private const float ScenarioPeriodSeconds = 26f;

    private readonly double _rateHz;
    private readonly double _startOffsetSeconds;
    private readonly byte[] _buffer = new byte[ForzaPacketFormat.PacketSize];

    private Thread? _thread;
    private volatile bool _stopRequested;

    /// <param name="rateHz">Emission rate. FH6 sends one packet per rendered frame.</param>
    /// <param name="startOffsetSeconds">
    /// Scenario time to begin at. Lets a snapshot or a test jump straight to the interesting
    /// part of the script instead of waiting for it.
    /// </param>
    public SyntheticTelemetrySource(double rateHz = 60, double startOffsetSeconds = 0)
    {
        _rateHz = rateHz;
        _startOffsetSeconds = startOffsetSeconds;
    }

    public event TelemetryPacketHandler? PacketReceived;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Simulated car state for the current instant of the scripted lap.</summary>
    private record struct Sim(
        float Speed, float Throttle, float Brake, float Steer, int Gear,
        float LongitudinalG, float LateralG, float Roll, float PitchRate, float YawRate, float Boost,
        float SlipRatioFront, float SlipRatioRear, float SlipAngleFront, float SlipAngleRear,
        float WheelSpinFront, float WheelSpinRear, bool LockedRear, float Power, float Torque);

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _stopRequested = false;
        _thread = new Thread(EmitLoop)
        {
            IsBackground = true,
            Name = "ForzaHud.Synthetic",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        var thread = _thread;
        if (thread is not null && thread.IsAlive && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _thread = null;
    }

    public void Dispose() => Stop();

    private void EmitLoop()
    {
        var period = TimeSpan.FromSeconds(1d / _rateHz);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nextEmit = TimeSpan.Zero;

        var gear = 1;
        var speed = 12f;
        var previousSpeed = speed;

        while (!_stopRequested)
        {
            var now = clock.Elapsed;
            if (now < nextEmit)
            {
                Thread.Sleep(1);
                continue;
            }

            nextEmit += period;
            if (nextEmit < now)
            {
                nextEmit = now + period;
            }

            var t = (float)((now.TotalSeconds + _startOffsetSeconds) % ScenarioPeriodSeconds);
            var sim = Evaluate(t, ref speed, ref gear, ref previousSpeed, (float)period.TotalSeconds);

            Write(sim, now);
            PacketReceived?.Invoke(_buffer, now);
        }
    }

    /// <summary>
    /// A repeatable driving script: launch, brake into a corner, hold the corner at the
    /// limit, then spin the rear tyres on exit.
    /// </summary>
    private static Sim Evaluate(float t, ref float speed, ref int gear, ref float previousSpeed, float dt)
    {
        float throttle = 0f, brake = 0f, steer = 0f;
        float lateralG = 0f, longitudinalG;
        float slipRatioFront = 0.05f, slipRatioRear = 0.06f;
        float slipAngleFront = 0.02f, slipAngleRear = 0.02f;
        bool lockedRear = false;
        float targetSpeed;

        if (t < 7f)
        {
            // Full-throttle acceleration through the gears.
            throttle = 1f;
            targetSpeed = 68f;
        }
        else if (t < 11f)
        {
            // Heavy braking; rear tyres briefly lock.
            brake = 1f;
            targetSpeed = 26f;
            lockedRear = t < 11.4f && speed > 30f;
            slipRatioRear = -1.35f;
            slipRatioFront = -0.55f;
        }
        else if (t < 17f)
        {
            // Long left-hander held near the limit of front grip.
            throttle = 0.72f;
            targetSpeed = 31f;
            steer = -0.62f;
            lateralG = 1.15f;
            slipAngleFront = 0.92f;
            slipAngleRear = 0.58f;
            slipRatioRear = 0.42f;
        }
        else if (t < 20.5f)
        {
            // Corner exit with rear wheelspin.
            throttle = 1f;
            targetSpeed = 52f;
            steer = -0.18f;
            lateralG = 0.42f;
            slipRatioRear = 1.28f;
            slipAngleRear = 0.44f;
        }
        else
        {
            // Cruise with a slow steering sweep, used to exercise the steer indicator.
            throttle = 0.28f;
            targetSpeed = 40f;
            steer = MathF.Sin((t - 20.5f) * 1.35f) * 0.85f;
            lateralG = steer * 0.55f;
            slipAngleFront = MathF.Abs(steer) * 0.35f;
        }

        var acceleration = (targetSpeed - speed) / 0.85f;
        previousSpeed = speed;
        speed = Math.Clamp(speed + acceleration * dt, 0f, 90f);
        longitudinalG = dt > 0 ? (speed - previousSpeed) / dt / 9.80665f : 0f;

        var rpm = EngineRpmFor(speed, gear);
        if (rpm > UpshiftRpm && gear < GearRatios.Length)
        {
            gear++;
        }
        else if (rpm < DownshiftRpm && gear > 1)
        {
            gear--;
        }
        rpm = EngineRpmFor(speed, gear);

        // A shaped power curve peaking near 5900 rpm, with boost building off idle.
        var normalized = Math.Clamp((rpm - IdleRpm) / (MaxRpm - IdleRpm), 0f, 1f);
        var power = 330_000f * MathF.Sin(MathF.PI * MathF.Pow(normalized, 0.85f)) * (0.35f + 0.65f * throttle);
        var boost = Math.Clamp(15.5f * throttle * MathF.Pow(normalized, 1.6f) - 6.5f, -6.5f, 15.5f);
        var torque = rpm > IdleRpm ? power / (rpm * MathF.PI / 30f) : 0f;

        var wheelSpeed = speed / WheelCircumferenceMeters * MathF.PI * 2f;
        var roll = -lateralG * 0.05f;

        return new Sim(
            Speed: speed, Throttle: throttle, Brake: brake, Steer: steer, Gear: gear,
            LongitudinalG: Math.Clamp(longitudinalG, -2f, 2f), LateralG: lateralG,
            Roll: roll,
            PitchRate: -longitudinalG * 0.08f,
            YawRate: lateralG * 0.35f,
            Boost: boost,
            SlipRatioFront: slipRatioFront, SlipRatioRear: slipRatioRear,
            SlipAngleFront: slipAngleFront, SlipAngleRear: slipAngleRear,
            WheelSpinFront: wheelSpeed * (1f + MathF.Max(0f, slipRatioFront) * 0.35f),
            WheelSpinRear: lockedRear ? 0.4f : wheelSpeed * (1f + MathF.Max(0f, slipRatioRear) * 0.35f),
            LockedRear: lockedRear,
            Power: power, Torque: torque);
    }

    private static float EngineRpmFor(float speed, int gear)
    {
        var ratio = GearRatios[Math.Clamp(gear - 1, 0, GearRatios.Length - 1)];
        var rpm = speed / WheelCircumferenceMeters * 60f * ratio * FinalDrive;
        return Math.Clamp(rpm, IdleRpm, MaxRpm);
    }

    private void Write(in Sim sim, TimeSpan timestamp)
    {
        Array.Clear(_buffer);
        var rpm = EngineRpmFor(sim.Speed, sim.Gear);

        WriteInt32(IsRaceOn, 1);
        WriteUInt32(TimestampMs, (uint)(timestamp.TotalMilliseconds % uint.MaxValue));
        WriteSingle(EngineMaxRpm, MaxRpm);
        WriteSingle(EngineIdleRpm, IdleRpm);
        WriteSingle(CurrentEngineRpm, rpm);

        // Car local space: X = right, Y = up, Z = forward.
        WriteSingle(AccelerationX, sim.LateralG * 9.80665f);
        WriteSingle(AccelerationZ, sim.LongitudinalG * 9.80665f);
        WriteSingle(AngularVelocityX, sim.PitchRate);
        WriteSingle(AngularVelocityY, sim.YawRate);
        WriteSingle(Roll, sim.Roll);

        WriteSingle(VelocityZ, sim.Speed);
        WriteSingle(Speed, sim.Speed);
        WriteSingle(Power, sim.Power);
        WriteSingle(Torque, sim.Torque);
        WriteSingle(Boost, sim.Boost);

        _buffer[Accel] = ToByte(sim.Throttle);
        _buffer[Brake] = ToByte(sim.Brake);
        _buffer[Clutch] = 0;
        _buffer[HandBrake] = 0;
        _buffer[Gear] = (byte)sim.Gear;
        _buffer[Steer] = (byte)(sbyte)Math.Clamp(sim.Steer * 127f, -127f, 127f);

        WriteWheel(TireSlipRatio, sim.SlipRatioFront, sim.SlipRatioRear);
        WriteWheel(TireSlipAngle, sim.SlipAngleFront, sim.SlipAngleRear);
        WriteWheel(TireCombinedSlip, sim.SlipRatioFront + sim.SlipAngleFront, sim.SlipRatioRear + sim.SlipAngleRear);
        WriteWheel(WheelRotationSpeed, sim.WheelSpinFront, sim.WheelSpinRear);

        WriteInt32(CarOrdinal, 1234);
        WriteInt32(DrivetrainType, 2);
        WriteInt32(NumCylinders, 6);

        static byte ToByte(float value) => (byte)Math.Clamp(value * 255f, 0f, 255f);
    }

    private void WriteWheel(int offset, float front, float rear)
    {
        WriteSingle(offset + 0, front);
        WriteSingle(offset + 4, front);
        WriteSingle(offset + 8, rear);
        WriteSingle(offset + 12, rear);
    }

    private void WriteSingle(int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(offset, 4), value);

    private void WriteInt32(int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset, 4), value);

    private void WriteUInt32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset, 4), value);
}
