using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Detects traction-control intervention from engine power, RPM, throttle, and driven-wheel slip.
/// The detector deliberately latches only after repeated evidence and discards observations that
/// could have been caused by a shift, limiter, transmission transient, or telemetry discontinuity.
/// </summary>
public sealed class TractionControlAnalyzer
{
    private readonly TractionControlSettings _settings;
    private readonly WheelSpeedTcsAnalyzer _wheelSpeed;
    private readonly TireSlipTcsAnalyzer _tireSlip;
    private readonly List<Observation> _history = [];

    private TelemetrySnapshot? _previous;
    private bool _active;
    private int _activationSamples;
    private int _deactivationSamples;
    private TimeSpan? _attackStartedAt;
    private TimeSpan? _releaseStartedAt;
    private int _suppressionSamples;
    private TimeSpan _suppressedUntil;
    private bool _slipEvidenceLatched;

    public TractionControlAnalyzer(TractionControlSettings settings)
    {
        _settings = settings;
        _settings.WheelSpeed ??= new WheelSpeedTcsSettings();
        _settings.TireSlip ??= new TireSlipTcsSettings();
        _wheelSpeed = new WheelSpeedTcsAnalyzer(_settings.WheelSpeed);
        _tireSlip = new TireSlipTcsAnalyzer(_settings.TireSlip);
    }

    /// <summary>Whether the latest frame should be excluded from power-curve learning.</summary>
    public bool PowerSampleAffectedByTcs { get; private set; }

    /// <summary>Latest wheel-speed-only evidence, before outer TCS fusion.</summary>
    public WheelSpeedTcsEvidence WheelSpeedEvidence => _wheelSpeed.Evidence;

    /// <summary>Feeds one telemetry frame and returns the hysteresis-filtered TCS state.</summary>
    public bool Update(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PowerSampleAffectedByTcs = false;

        if (!_settings.Enabled || !HasUsableFrame(snapshot))
        {
            Reset();
            return false;
        }

        var throttleIsQualifying = _previous is { } previousSnapshot
                                    && IsHighAndStableThrottle(previousSnapshot, snapshot);
        var wheelEvidence = _wheelSpeed.Update(snapshot);
        var tireSlipActive = _tireSlip.Update(snapshot, throttleIsQualifying);
        var wheelInterventionEvidence = wheelEvidence.CombinedConfidence > 0f
                                        && wheelEvidence.CombinedConfidence
                                            >= _settings.WheelSpeed.ConfirmationConfidence * 0.75f
                                        && throttleIsQualifying;

        if (_previous is { } previous)
        {
            if (VehicleIdentity.From(previous) != VehicleIdentity.From(snapshot)
                || IsTelemetryGap(previous, snapshot))
            {
                _history.Clear();
                _active = false;
                _activationSamples = 0;
                _deactivationSamples = 0;
                _attackStartedAt = null;
                _releaseStartedAt = null;
                _suppressionSamples = 0;
                _suppressedUntil = TimeSpan.Zero;
                _slipEvidenceLatched = false;
                _tireSlip.Reset();
                _previous = snapshot;
                return false;
            }

            if (IsSuppressed(snapshot))
            {
                _previous = snapshot;
                _active = tireSlipActive;
                PowerSampleAffectedByTcs = tireSlipActive;
                return tireSlipActive;
            }

            if (IsShift(previous, snapshot) || HasTransmissionInput(previous, snapshot))
            {
                StartSuppression(snapshot);
                _active = tireSlipActive;
                PowerSampleAffectedByTcs = tireSlipActive;
                return tireSlipActive;
            }

            if (IsLimiterEvent(previous, snapshot))
            {
                Reset();
                _previous = snapshot;
                return false;
            }
        }
        else
        {
            _previous = snapshot;
            AddObservation(snapshot, cut: false);
            return false;
        }

        if (!throttleIsQualifying)
        {
            _history.Clear();
            _slipEvidenceLatched = false;
            _previous = snapshot;
            var active = UpdateHysteresis(tireSlipActive || wheelInterventionEvidence, snapshot);
            PowerSampleAffectedByTcs = active || tireSlipActive || wheelInterventionEvidence;
            return active;
        }

        var previousObservation = _history.Count == 0
            ? new Observation(_previous!.CurrentEngineRpm, _previous.Power, DrivenWheelSlip(_previous))
            : _history[^1];
        var currentSlip = DrivenWheelSlip(snapshot);
        var slipIncreasing = currentSlip >= _settings.MinimumDrivenSlip
            && currentSlip - previousObservation.DrivenWheelSlip >= _settings.MinimumDrivenSlipIncrease;
        var cut = IsTransientPowerCut(snapshot, previousObservation);
        var repeatedCut = CountRecentCuts() + (cut ? 1 : 0) >= _settings.MinimumRepeatedCuts;
        var transientEvidence = cut || repeatedCut;
        var slipSuppressionEvidence = IsSlipCorrelatedSuppression(
            snapshot,
            previousObservation,
            slipIncreasing);
        var powerRecovered = snapshot.Power > previousObservation.Power
                              * (1f + _settings.MaximumPowerRiseFraction);
        if (powerRecovered)
        {
            _slipEvidenceLatched = false;
        }

        var slipEvidence = slipIncreasing
                           || (_slipEvidenceLatched && currentSlip >= _settings.MinimumDrivenSlip);
        if (slipIncreasing)
        {
            _slipEvidenceLatched = true;
        }

        AddObservation(snapshot, cut);
        _previous = snapshot;

        var engineEvidence = _settings.EnginePowerEvidenceEnabled
                             && (transientEvidence || slipSuppressionEvidence || slipEvidence);
        var isActive = UpdateHysteresis(
            tireSlipActive || wheelInterventionEvidence || engineEvidence,
            snapshot);
        PowerSampleAffectedByTcs = isActive || tireSlipActive || engineEvidence || wheelInterventionEvidence;
        return isActive;
    }

    /// <summary>Clears all temporal evidence and turns the indicator off.</summary>
    public void Reset()
    {
        _previous = null;
        _history.Clear();
        _active = false;
        _activationSamples = 0;
        _deactivationSamples = 0;
        _attackStartedAt = null;
        _releaseStartedAt = null;
        _suppressionSamples = 0;
        _suppressedUntil = TimeSpan.Zero;
        _slipEvidenceLatched = false;
        _wheelSpeed.Reset();
        _tireSlip.Reset();
        PowerSampleAffectedByTcs = false;
    }

    private bool IsTransientPowerCut(TelemetrySnapshot snapshot, Observation previous)
    {
        if (previous.Power <= 0f || snapshot.Power < 0f || _history.Count == 0)
        {
            return false;
        }

        var baseline = _history
            .TakeLast(Math.Max(1, _settings.TrendSampleCount))
            .Where(sample => sample.Power > 0f)
            .Select(sample => sample.Power)
            .DefaultIfEmpty(previous.Power)
            .Average();
        var precedingDecline = _history.Count >= 2
            ? _history[^2].Power - previous.Power
            : 0f;

        var belowTrend = snapshot.Power <= baseline * (1f - _settings.TransientPowerDropFraction);
        var abruptCut = belowTrend
            && snapshot.Power <= previous.Power * (1f - _settings.TransientPowerDropFraction)
            && precedingDecline <= baseline * _settings.MaximumPreCutDeclineFraction;
        var sustainedCut = previous.WasCut && belowTrend;
        return abruptCut || sustainedCut;
    }

    private bool IsSlipCorrelatedSuppression(
        TelemetrySnapshot snapshot,
        Observation previous,
        bool slipIncreasing)
    {
        if (!slipIncreasing
            || snapshot.CurrentEngineRpm - previous.Rpm < _settings.MinimumRpmIncrease
            || previous.Power <= 0f
            || snapshot.Power < 0f)
        {
            return false;
        }

        var currentSlope = snapshot.Power - previous.Power;
        var previousSlope = _history.Count >= 2
            ? previous.Power - _history[^2].Power
            : 0f;
        var allowedRise = MathF.Max(
            previous.Power * _settings.MaximumPowerRiseFraction,
            MathF.Max(0f, previousSlope) * 0.25f);

        return currentSlope <= allowedRise;
    }

    private int CountRecentCuts() =>
        _history
            .TakeLast(Math.Max(1, _settings.RepeatedCutWindowSamples))
            .Count(sample => sample.WasCut);

    private bool IsHighAndStableThrottle(TelemetrySnapshot previous, TelemetrySnapshot current) =>
        previous.Throttle >= _settings.MinimumThrottle
        && current.Throttle >= _settings.MinimumThrottle
        && current.Throttle >= previous.Throttle - _settings.MaximumThrottleDecrease;

    /// <summary>
    /// Telemetry gap check specific to TCS detection. Discontinuous frame arrivals
    /// reset detector hysteresis so dropouts do not trigger false positive TCS intervention.
    /// </summary>
    private bool IsTelemetryGap(TelemetrySnapshot previous, TelemetrySnapshot current)
    {
        if (previous.ReceivedAt <= TimeSpan.Zero || current.ReceivedAt <= TimeSpan.Zero)
        {
            return false;
        }

        var delta = current.ReceivedAt - previous.ReceivedAt;
        return delta <= TimeSpan.Zero
            || delta.TotalMilliseconds > _settings.MaximumTelemetryGapMilliseconds;
    }

    private bool IsSuppressed(TelemetrySnapshot snapshot)
    {
        if (_suppressionSamples > 0)
        {
            _suppressionSamples--;
            return true;
        }

        return _suppressedUntil > TimeSpan.Zero && snapshot.ReceivedAt < _suppressedUntil;
    }

    /// <summary>
    /// Gear shift check specific to TCS detection. Momentary engine deceleration during
    /// shifts is suppressed to avoid falsely latching the TCS indicator.
    /// </summary>
    private bool IsShift(TelemetrySnapshot previous, TelemetrySnapshot current) =>
        previous.Gear != current.Gear
        || previous.CurrentEngineRpm - current.CurrentEngineRpm
        > MathF.Max(_settings.MinimumShiftRpmDrop, previous.CurrentEngineRpm * 0.08f);

    private bool HasTransmissionInput(TelemetrySnapshot previous, TelemetrySnapshot current) =>
        previous.Clutch > _settings.MaximumClutch
        || current.Clutch > _settings.MaximumClutch
        || previous.Brake > _settings.MaximumBrake
        || current.Brake > _settings.MaximumBrake
        || previous.HandBrake > _settings.MaximumHandBrake
        || current.HandBrake > _settings.MaximumHandBrake;

    private bool IsLimiterEvent(TelemetrySnapshot previous, TelemetrySnapshot current)
    {
        var previousAtLimiter = previous.CurrentEngineRpm
            >= previous.EngineMaxRpm * _settings.LimiterRpmFraction;
        var currentAtLimiter = current.CurrentEngineRpm
            >= current.EngineMaxRpm * _settings.LimiterRpmFraction;
        return previousAtLimiter || currentAtLimiter;
    }

    private void StartSuppression(TelemetrySnapshot snapshot)
    {
        _history.Clear();
        _wheelSpeed.Reset();
        _active = false;
        _activationSamples = 0;
        _deactivationSamples = 0;
        _suppressionSamples = Math.Max(1, _settings.ShiftSuppressionSamples);
        _suppressedUntil = snapshot.ReceivedAt > TimeSpan.Zero
            ? snapshot.ReceivedAt + TimeSpan.FromMilliseconds(_settings.ShiftSuppressionMilliseconds)
            : TimeSpan.Zero;
        _slipEvidenceLatched = false;
        _previous = snapshot;
    }

    private bool UpdateHysteresis(bool evidence, TelemetrySnapshot snapshot)
    {
        if (evidence)
        {
            if (_activationSamples == 0)
            {
                _attackStartedAt = HasTimestamp(snapshot) ? snapshot.ReceivedAt : null;
            }

            _activationSamples++;
            _deactivationSamples = 0;
            _releaseStartedAt = null;
            if (!_active
                && _activationSamples >= Math.Max(1, _settings.ActivationSamples)
                && HasElapsed(
                    _attackStartedAt,
                    snapshot.ReceivedAt,
                    _settings.AttackMilliseconds))
            {
                _active = true;
            }
        }
        else
        {
            _activationSamples = 0;
            _attackStartedAt = null;
            if (_active)
            {
                if (_deactivationSamples == 0)
                {
                    _releaseStartedAt = HasTimestamp(snapshot) ? snapshot.ReceivedAt : null;
                }

                _deactivationSamples++;
                if (_deactivationSamples >= Math.Max(1, _settings.DeactivationSamples)
                    && HasElapsed(
                        _releaseStartedAt,
                        snapshot.ReceivedAt,
                        _settings.ReleaseMilliseconds))
                {
                    _active = false;
                    _releaseStartedAt = null;
                }
            }
            else
            {
                _deactivationSamples = 0;
                _releaseStartedAt = null;
            }
        }

        return _active;
    }

    private static bool HasTimestamp(TelemetrySnapshot snapshot) =>
        snapshot.ReceivedAt > TimeSpan.Zero;

    private static bool HasElapsed(TimeSpan? startedAt, TimeSpan current, double milliseconds) =>
        milliseconds <= 0
        || startedAt is null
        || current <= TimeSpan.Zero
        || (current - startedAt.Value).TotalMilliseconds >= milliseconds;

    private void AddObservation(TelemetrySnapshot snapshot, bool cut)
    {
        _history.Add(new Observation(
            snapshot.CurrentEngineRpm,
            snapshot.Power,
            DrivenWheelSlip(snapshot),
            cut));
        var maximumHistory = Math.Max(
            _settings.TrendSampleCount,
            _settings.RepeatedCutWindowSamples) + 2;
        if (_history.Count > maximumHistory)
        {
            _history.RemoveRange(0, _history.Count - maximumHistory);
        }
    }

    private static bool HasUsableFrame(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm
        && snapshot.EngineMaxRpm > 0f
        && snapshot.CurrentEngineRpm >= snapshot.EngineIdleRpm
        && snapshot.CurrentEngineRpm <= snapshot.EngineMaxRpm
        && snapshot.Gear is >= 1 and <= 10
        && float.IsFinite(snapshot.Power)
        && float.IsFinite(snapshot.Throttle);

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

    private readonly record struct Observation(
        float Rpm,
        float Power,
        float DrivenWheelSlip,
        bool WasCut = false);
}
