using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Collects hard-valid, non-TCS telemetry as raw observations. Binning and shape cleaning are
/// deferred until a curve is requested so the cleaner can refit from the underlying samples.
/// </summary>
internal sealed class PowerCurveRecorder
{
    private readonly PowerbandSettings _settings;
    private readonly TractionControlSettings _tractionControl;
    private readonly int _binCount;
    private readonly List<PowerCurveSample> _samples = [];
    private readonly List<PowerCurveBinConfidence> _binConfidence = [];
    private readonly Dictionary<int, int> _rejectedByBin = [];
    private float _idleRpm;
    private float _maxRpm;
    private TelemetrySnapshot? _previous;
    private int _shiftSuppressionSamples;
    private TimeSpan _shiftSuppressedUntil;
    private bool _requiresStableFrame;

    public PowerCurveRecorder(
        PowerbandSettings settings,
        TractionControlSettings? tractionControl = null)
    {
        _settings = settings;
        _tractionControl = tractionControl ?? new TractionControlSettings();
        _binCount = Math.Clamp(settings.BinCount, 8, 256);
    }

    public int AcceptedPowerSamples => _samples.Count;

    public int RejectedPowerSamples { get; private set; }

    public bool HasRpmRange => _maxRpm > _idleRpm;

    public int PopulatedRpmBins =>
        _samples
            .Select(sample => BinFor(sample.Rpm))
            .Where(bin => bin >= 0)
            .Distinct()
            .Count();

    public float RpmCoverageFraction
    {
        get
        {
            var bins = _samples
                .Select(sample => BinFor(sample.Rpm))
                .Where(bin => bin >= 0)
                .Distinct()
                .OrderBy(bin => bin)
                .ToArray();
            return bins.Length == 0
                ? 0f
                : (bins[^1] - bins[0] + 1f) / _binCount;
        }
    }

    public IReadOnlyList<PowerCurveSample> RawPowerSamples => _samples.ToArray();

    public IReadOnlyList<PowerCurveBinConfidence> BinConfidence => _binConfidence.ToArray();

    public void Reset()
    {
        _samples.Clear();
        _binConfidence.Clear();
        _rejectedByBin.Clear();
        RejectedPowerSamples = 0;
        _idleRpm = 0f;
        _maxRpm = 0f;
        ResetTemporalState();
    }

    public void ResetTemporalState()
    {
        _previous = null;
        _shiftSuppressionSamples = 0;
        _shiftSuppressedUntil = TimeSpan.Zero;
        _requiresStableFrame = false;
    }

    public void SetRpmRange(float idleRpm, float maxRpm)
    {
        _idleRpm = idleRpm;
        _maxRpm = maxRpm;
        TrimSamples();
    }

    public void Seed(IReadOnlyList<PowerCurveSample> samples)
    {
        _samples.AddRange(samples.Where(IsFinite));
        TrimSamples();
    }

    public void Record(TelemetrySnapshot snapshot, bool includePowerSample)
    {
        var previous = _previous;
        var telemetryGap = previous is { } prior && IsTelemetryGap(prior, snapshot);
        if (telemetryGap)
        {
            _requiresStableFrame = true;
        }

        var shiftDetected = previous is { } shiftPrevious && IsShift(shiftPrevious, snapshot);
        if (shiftDetected)
        {
            StartShiftSuppression(snapshot);
        }

        var suppressed = IsSuppressed(snapshot);
        if (includePowerSample
            && !_requiresStableFrame
            && IsValidPowerSample(snapshot, previous, suppressed))
        {
            AddSample(snapshot, previous);
        }

        if (_requiresStableFrame && !telemetryGap)
        {
            _requiresStableFrame = false;
        }

        _previous = snapshot;
    }

    public List<PowerCurvePoint> CreatePowerCurve()
    {
        var result = PowerCurveCleaner.Clean(
            _samples,
            _idleRpm,
            _maxRpm,
            _settings,
            _tractionControl,
            _rejectedByBin);
        _samples.Clear();
        _samples.AddRange(result.Samples);
        _binConfidence.Clear();
        _binConfidence.AddRange(result.BinConfidence);
        _rejectedByBin.Clear();
        foreach (var (bin, count) in result.RejectedByBin)
        {
            _rejectedByBin[bin] = count;
        }
        RejectedPowerSamples += result.RejectedSampleCount;
        return result.Curve.ToList();
    }

    private bool IsValidPowerSample(
        TelemetrySnapshot snapshot,
        TelemetrySnapshot? previous,
        bool suppressed)
    {
        if (!IsFiniteSnapshot(snapshot)
            || snapshot.Gear is < 1 or > 10
            || snapshot.Throttle < _settings.MinimumThrottle
            || snapshot.Speed < _settings.MinimumSpeed
            || snapshot.Power <= 0f
            || snapshot.CurrentEngineRpm < snapshot.EngineIdleRpm
            || snapshot.CurrentEngineRpm > snapshot.EngineMaxRpm
            || snapshot.CurrentEngineRpm
                >= snapshot.EngineMaxRpm
                    * Math.Clamp(_settings.EffectiveRedlinePercentile, 0f, 1f))
        {
            return false;
        }

        if (previous is not { } prior)
        {
            return true;
        }

        if (suppressed
            || prior.Gear != snapshot.Gear
            || snapshot.Throttle < prior.Throttle - _tractionControl.MaximumThrottleDecrease
            || !IsStableInput(prior)
            || !IsStableInput(snapshot)
            || IsTelemetryGap(prior, snapshot)
            || snapshot.CurrentEngineRpm < prior.CurrentEngineRpm
            || !IsNormalRpmRate(prior, snapshot))
        {
            return false;
        }

        return true;
    }

    private bool IsNormalRpmRate(TelemetrySnapshot previous, TelemetrySnapshot current)
    {
        if (previous.ReceivedAt <= TimeSpan.Zero || current.ReceivedAt <= previous.ReceivedAt)
        {
            return true;
        }

        var seconds = (current.ReceivedAt - previous.ReceivedAt).TotalSeconds;
        var rpmRate = (current.CurrentEngineRpm - previous.CurrentEngineRpm) / (float)seconds;
        return rpmRate >= _settings.MinimumRpmRate
            && rpmRate <= _settings.MaximumRpmRate;
    }

    private bool IsStableInput(TelemetrySnapshot snapshot) =>
        snapshot.Clutch <= _tractionControl.MaximumClutch
        && snapshot.Brake <= _tractionControl.MaximumBrake
        && snapshot.HandBrake <= _tractionControl.MaximumHandBrake;

    /// <summary>
    /// Telemetry gap check specific to power-curve recording. Packet dropouts invalidate
    /// rate-of-change continuity for wide-open-throttle curve points.
    /// </summary>
    private bool IsTelemetryGap(TelemetrySnapshot previous, TelemetrySnapshot current)
    {
        if (previous.ReceivedAt <= TimeSpan.Zero || current.ReceivedAt <= TimeSpan.Zero)
        {
            return false;
        }

        var delta = current.ReceivedAt - previous.ReceivedAt;
        return delta <= TimeSpan.Zero
            || delta.TotalMilliseconds > _tractionControl.MaximumTelemetryGapMilliseconds;
    }

    /// <summary>
    /// Gear shift check specific to power-curve recording. Suppresses power observation
    /// during shifting to prevent clutching transients from entering the dyno curve.
    /// </summary>
    private bool IsShift(TelemetrySnapshot previous, TelemetrySnapshot current) =>
        previous.Gear != current.Gear
        || previous.CurrentEngineRpm - current.CurrentEngineRpm
            > _tractionControl.MinimumShiftRpmDrop;

    private void StartShiftSuppression(TelemetrySnapshot snapshot)
    {
        _shiftSuppressionSamples = Math.Max(1, _tractionControl.ShiftSuppressionSamples);
        _shiftSuppressedUntil = snapshot.ReceivedAt > TimeSpan.Zero
            ? snapshot.ReceivedAt
                + TimeSpan.FromMilliseconds(_tractionControl.ShiftSuppressionMilliseconds)
            : TimeSpan.Zero;
    }

    private bool IsSuppressed(TelemetrySnapshot snapshot)
    {
        if (_shiftSuppressionSamples > 0)
        {
            _shiftSuppressionSamples--;
            return true;
        }

        return _shiftSuppressedUntil > TimeSpan.Zero && snapshot.ReceivedAt < _shiftSuppressedUntil;
    }

    private void AddSample(TelemetrySnapshot snapshot, TelemetrySnapshot? previous)
    {
        var rpmRate = 0f;
        var longitudinalAcceleration = 0f;
        if (previous is { } prior
            && prior.ReceivedAt > TimeSpan.Zero
            && snapshot.ReceivedAt > prior.ReceivedAt)
        {
            var seconds = (float)(snapshot.ReceivedAt - prior.ReceivedAt).TotalSeconds;
            rpmRate = (snapshot.CurrentEngineRpm - prior.CurrentEngineRpm) / seconds;
            longitudinalAcceleration = (snapshot.Speed - prior.Speed) / seconds;
        }

        _samples.Add(new PowerCurveSample(
            snapshot.CurrentEngineRpm,
            snapshot.Power,
            snapshot.Throttle,
            snapshot.Gear,
            rpmRate,
            DrivenWheelSlip(snapshot),
            snapshot.ReceivedAt)
        {
            SpeedMetersPerSecond = snapshot.Speed,
            LongitudinalAcceleration = longitudinalAcceleration,
            DrivetrainType = snapshot.DrivetrainType,
        });
        TrimSamples();
    }

    private void TrimSamples()
    {
        var maximumPerBin = Math.Max(1, _settings.MaximumSamplesPerBin);
        while (true)
        {
            var overfullBin = _samples
                .Select((sample, index) => (sample, index))
                .Where(pair => BinFor(pair.sample.Rpm) >= 0)
                .GroupBy(pair => BinFor(pair.sample.Rpm))
                .FirstOrDefault(group => group.Count() > maximumPerBin);
            if (overfullBin is null)
            {
                break;
            }

            _samples.RemoveAt(overfullBin.First().index);
        }

        var maximum = Math.Max(1, _settings.MaximumRawSamples);
        if (_samples.Count > maximum)
        {
            _samples.RemoveRange(0, _samples.Count - maximum);
        }
    }

    private static bool IsFiniteSnapshot(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && float.IsFinite(snapshot.EngineMaxRpm)
        && float.IsFinite(snapshot.EngineIdleRpm)
        && float.IsFinite(snapshot.CurrentEngineRpm)
        && float.IsFinite(snapshot.Power)
        && float.IsFinite(snapshot.Throttle)
        && float.IsFinite(snapshot.Speed)
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm
        && snapshot.EngineMaxRpm > 0f;

    private static bool IsFinite(PowerCurveSample sample) =>
        float.IsFinite(sample.Rpm)
        && float.IsFinite(sample.Power)
        && float.IsFinite(sample.Throttle)
        && float.IsFinite(sample.RpmRate)
        && float.IsFinite(sample.WheelSlip)
        && sample.Rpm > 0f
        && sample.Power > 0f;

    private int BinFor(float rpm)
    {
        if (_maxRpm <= _idleRpm || rpm < _idleRpm || rpm > _maxRpm)
        {
            return -1;
        }

        var bin = (int)((rpm - _idleRpm) / (_maxRpm - _idleRpm) * _binCount);
        return Math.Clamp(bin, 0, _binCount - 1);
    }

    private static float DrivenWheelSlip(TelemetrySnapshot snapshot)
    {
        var slip = snapshot.TireSlipRatio;
        return snapshot.DrivetrainType switch
        {
            0 => MathF.Max(MathF.Abs(slip.FrontLeft), MathF.Abs(slip.FrontRight)),
            1 => MathF.Max(MathF.Abs(slip.RearLeft), MathF.Abs(slip.RearRight)),
            2 => MathF.Max(
                MathF.Max(MathF.Abs(slip.FrontLeft), MathF.Abs(slip.FrontRight)),
                MathF.Max(MathF.Abs(slip.RearLeft), MathF.Abs(slip.RearRight))),
            _ => 0f,
        };
    }
}
