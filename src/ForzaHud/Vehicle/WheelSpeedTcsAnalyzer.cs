using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Extracts wheel-speed-only evidence from a preceding driven-wheel overspeed and its rapid
/// suppression. It deliberately does not know about throttle, power, RPM, brakes, or shifts.
/// </summary>
public sealed class WheelSpeedTcsAnalyzer
{
    private const float StrongCollapseRate = 5f;
    private const float ModulationWeight = 0.20f;
    private const float ReconvergenceWeight = 0.15f;
    private const int MinimumModulationEvents = 2;
    private const int InterventionHoldSamples = 8;
    private const int RecoverySamples = 12;
    private const double MaximumTelemetryGapMilliseconds = 150;
    private const float MaximumWheelSpeed = 10000f;
    private const float MaximumWheelSpeedJump = 250f;
    private const float MaximumWheelSpeedJumpFraction = 2.5f;

    private readonly WheelSpeedTcsSettings _settings;
    private readonly List<TimeSpan> _collapseEvents = [];
    private readonly float[] _localBaseline = new float[4];

    private VehicleIdentity? _identity;
    private WheelFrame? _previous;
    private float _frontRearRatio = 1f;
    private bool _hasFrontRearRatio;
    private bool _spinLatched;
    private bool _collapseRecorded;
    private float _peakExcess;
    private TimeSpan _peakAt;
    private WheelSpeedTcsState _state = WheelSpeedTcsState.Normal;
    private int _confirmedSamples;
    private int _recoverySamples;
    private TimeSpan _confirmedAt;
    private TimeSpan _recoveryAt;
    private WheelValues<bool> _eventAffectedWheels;
    private WheelSpeedAxle _eventAffectedAxle = WheelSpeedAxle.Unknown;
    private bool _reconverged;

    public WheelSpeedTcsAnalyzer(WheelSpeedTcsSettings settings) => _settings = settings;

    /// <summary>Most recent wheel-speed diagnostics and evidence.</summary>
    public WheelSpeedTcsEvidence Evidence { get; private set; } = WheelSpeedTcsEvidence.Empty;

    public WheelSpeedTcsEvidence Update(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_settings.Enabled)
        {
            Reset();
            return Evidence;
        }

        if (!TryGetWheels(snapshot, out var raw))
        {
            Reset();
            return Evidence;
        }

        var identity = VehicleIdentity.From(snapshot);
        if (_identity is { } previousIdentity && previousIdentity != identity)
        {
            Reset();
        }

        _identity = identity;

        if (_previous is { } previous)
        {
            if (IsTelemetryGap(previous, snapshot) || IsImplausibleJump(previous.Raw, raw))
            {
                ResetTemporalState();
                _identity = identity;
            }
        }

        var filtered = Filter(raw, _previous?.Filtered ?? raw);
        if (!_hasFrontRearRatio)
        {
            var front = Mean(filtered.FrontLeft, filtered.FrontRight);
            var rear = Mean(filtered.RearLeft, filtered.RearRight);
            if (front >= _settings.MinimumLearningSpeed && rear >= _settings.MinimumLearningSpeed)
            {
                _frontRearRatio = Clamp(front / rear, 0.5f, 1.5f);
                _hasFrontRearRatio = true;
            }
        }

        var normalized = Normalize(filtered);
        var metrics = CalculateMetrics(snapshot.DrivetrainType, normalized);
        metrics = ApplyLocalBaseline(snapshot.DrivetrainType, metrics);
        var deltaSeconds = _previous is { } prior
            ? ElapsedSeconds(prior.Timestamp, snapshot.ReceivedAt)
            : 0f;
        var derivatives = _previous is { } previousFrame && deltaSeconds > 0f
            ? Derivatives(metrics, previousFrame.Metrics, deltaSeconds)
            : WheelDerivatives.Zero;

        var stable = _previous is null
            || (metrics.DrivenExcess <= _settings.MinimumDrivenExcess
                && MathF.Abs(derivatives.DrivenExcess) < _settings.MinimumSpinDerivative);
        if (stable)
        {
            LearnFrontRearRatio(filtered);
            LearnLocalBaseline(metrics.WheelExcess);
            normalized = Normalize(filtered);
            metrics = CalculateMetrics(snapshot.DrivetrainType, normalized);
            metrics = ApplyLocalBaseline(snapshot.DrivetrainType, metrics);
            if (_previous is { } previousAfterLearn && deltaSeconds > 0f)
            {
                derivatives = Derivatives(metrics, previousAfterLearn.Metrics, deltaSeconds);
            }
        }

        var evidence = Evaluate(snapshot, filtered, normalized, metrics, derivatives);
        _previous = new WheelFrame(snapshot.ReceivedAt, raw, filtered, metrics);
        Evidence = evidence;
        return evidence;
    }

    /// <summary>Clears ratio, derivative, peak, modulation, and event state.</summary>
    public void Reset()
    {
        ResetTemporalState();
        _identity = null;
        Evidence = WheelSpeedTcsEvidence.Empty;
    }

    private void ResetTemporalState()
    {
        _previous = null;
        _hasFrontRearRatio = false;
        _frontRearRatio = 1f;
        Array.Clear(_localBaseline);
        _collapseEvents.Clear();
        _spinLatched = false;
        _collapseRecorded = false;
        _peakExcess = 0f;
        _peakAt = TimeSpan.Zero;
        _state = WheelSpeedTcsState.Normal;
        _confirmedSamples = 0;
        _recoverySamples = 0;
        _confirmedAt = TimeSpan.Zero;
        _recoveryAt = TimeSpan.Zero;
        _eventAffectedWheels = default;
        _eventAffectedAxle = WheelSpeedAxle.Unknown;
        _reconverged = false;
    }

    private WheelSpeedTcsEvidence Evaluate(
        TelemetrySnapshot snapshot,
        WheelValues<float> filtered,
        WheelValues<float> normalized,
        WheelMetrics metrics,
        WheelDerivatives derivatives)
    {
        if (_spinLatched
            && _collapseRecorded
            && metrics.DrivenExcess <= _settings.MinimumDrivenExcess * 0.5f)
        {
            _reconverged = true;
        }

        var spinDeveloping = metrics.DrivenExcess >= _settings.MinimumDrivenExcess
                             && derivatives.DrivenExcess >= _settings.MinimumSpinDerivative;
        if (spinDeveloping)
        {
            if (_reconverged)
            {
                _collapseRecorded = false;
                _peakExcess = 0f;
                _eventAffectedWheels = default;
                _eventAffectedAxle = WheelSpeedAxle.Unknown;
                _reconverged = false;
            }

            var currentAffectedWheels = AffectedWheels(snapshot.DrivetrainType, metrics.WheelExcess);
            if (HasAffectedWheel(currentAffectedWheels))
            {
                _eventAffectedWheels = currentAffectedWheels;
                _eventAffectedAxle = AffectedAxle(snapshot.DrivetrainType, metrics.WheelExcess);
            }

            if (!_spinLatched)
            {
                _spinLatched = true;
                _peakExcess = metrics.DrivenExcess;
                _peakAt = snapshot.ReceivedAt;
                _collapseRecorded = false;
                _state = WheelSpeedTcsState.SpinDeveloping;
            }
            else if (metrics.DrivenExcess > _peakExcess)
            {
                _peakExcess = metrics.DrivenExcess;
                _peakAt = snapshot.ReceivedAt;
            }

            if (_peakExcess >= _settings.MinimumDrivenExcess)
            {
                _state = WheelSpeedTcsState.Spinning;
            }
        }
        else if (_spinLatched && metrics.DrivenExcess > _peakExcess)
        {
            _peakExcess = metrics.DrivenExcess;
            _peakAt = snapshot.ReceivedAt;
        }

        var peakDrop = MathF.Max(0f, _peakExcess - metrics.DrivenExcess);
        var collapseRate = CollapseRate(snapshot.ReceivedAt, peakDrop, derivatives.DrivenExcess);
        var collapseCandidate = _spinLatched
                                && peakDrop >= _settings.MinimumCollapse
                                && derivatives.DrivenExcess <= -_settings.MinimumCollapseRate;
        var collapseConfidence = collapseCandidate
            ? CollapseConfidence(peakDrop, collapseRate, derivatives.DrivenExcess)
            : 0f;

        if (collapseCandidate)
        {
            if (!_collapseRecorded)
            {
                _collapseEvents.Add(snapshot.ReceivedAt);
                _collapseRecorded = true;
            }

            TrimCollapseEvents(snapshot.ReceivedAt);
            _state = collapseConfidence >= _settings.ConfirmationConfidence
                ? WheelSpeedTcsState.InterventionConfirmed
                : WheelSpeedTcsState.InterventionCandidate;
            if (_state == WheelSpeedTcsState.InterventionConfirmed)
            {
                _confirmedSamples = 0;
                _confirmedAt = snapshot.ReceivedAt;
                _recoverySamples = 0;
            }
        }
        else if (_state == WheelSpeedTcsState.InterventionConfirmed)
        {
            _confirmedSamples++;
            if (!WithinHold(snapshot.ReceivedAt, _confirmedAt, _confirmedSamples))
            {
                _state = WheelSpeedTcsState.Recovery;
                _recoverySamples = 0;
                _recoveryAt = snapshot.ReceivedAt;
            }
        }
        else if (_state == WheelSpeedTcsState.Recovery)
        {
            _recoverySamples++;
            if (!WithinHold(snapshot.ReceivedAt, _recoveryAt, _recoverySamples, recovery: true))
            {
                _spinLatched = false;
                _collapseRecorded = false;
                _peakExcess = 0f;
                _state = WheelSpeedTcsState.Normal;
            }
        }
        else if (_spinLatched
                 && metrics.DrivenExcess <= _settings.MinimumDrivenExcess * 0.5f
                 && derivatives.DrivenExcess >= 0f)
        {
            _spinLatched = false;
            _collapseRecorded = false;
            _peakExcess = 0f;
            _state = WheelSpeedTcsState.Normal;
        }

        TrimCollapseEvents(snapshot.ReceivedAt);
        var spinConfidence = _spinLatched
            ? Clamp(_peakExcess / MathF.Max(_settings.MinimumDrivenExcess * 2f, 0.2f), 0f, 1f)
            : 0f;
        var modulationConfidence = ModulationConfidence();
        var reconvergenceConfidence = ReconvergenceConfidence(metrics.DrivenExcess);
        var interventionConfidence = MathF.Max(
            collapseConfidence,
            _state is WheelSpeedTcsState.InterventionConfirmed or WheelSpeedTcsState.Recovery
                ? 1f
                : 0f);
        var combinedConfidence = Clamp(
            interventionConfidence <= 0f
                ? 0f
                : spinConfidence * interventionConfidence
                  + ModulationWeight * modulationConfidence
                  + ReconvergenceWeight * reconvergenceConfidence,
            0f,
            1f);
        var affectedWheels = AffectedWheels(snapshot.DrivetrainType, metrics.WheelExcess);
        if (!HasAffectedWheel(affectedWheels) && _spinLatched)
        {
            affectedWheels = _eventAffectedWheels;
        }
        var affectedAxle = AffectedAxle(snapshot.DrivetrainType, metrics.WheelExcess);
        if (affectedAxle == WheelSpeedAxle.Unknown && _spinLatched)
        {
            affectedAxle = _eventAffectedAxle;
        }
        var observable = snapshot.DrivetrainType != 2
                          || affectedWheels.FrontLeft
                          || affectedWheels.FrontRight
                          || affectedWheels.RearLeft
                          || affectedWheels.RearRight
                          || MathF.Abs(metrics.FrontRearDifference) >= _settings.MinimumIndividualExcess;

        return new WheelSpeedTcsEvidence
        {
            FilteredWheelSpeeds = filtered,
            ReferenceCorrectedWheelSpeeds = normalized,
            WheelExcess = metrics.WheelExcess,
            WheelExcessDerivative = derivatives.WheelExcess,
            DrivenExcess = metrics.DrivenExcess,
            DrivenExcessDerivative = derivatives.DrivenExcess,
            FrontRearDifference = metrics.FrontRearDifference,
            FrontLeftRightDifference = metrics.FrontLeftRightDifference,
            RearLeftRightDifference = metrics.RearLeftRightDifference,
            FrontRearBaselineRatio = _frontRearRatio,
            RecentPeakExcess = _peakExcess,
            CollapseRate = collapseRate,
            SpinConfidence = spinConfidence,
            InterventionConfidence = interventionConfidence,
            ModulationConfidence = modulationConfidence,
            ReconvergenceConfidence = reconvergenceConfidence,
            CombinedConfidence = combinedConfidence,
            AffectedWheels = affectedWheels,
            AffectedAxle = affectedAxle,
            State = _state,
            Observable = observable,
        };
    }

    private void LearnFrontRearRatio(WheelValues<float> filtered)
    {
        var front = Mean(filtered.FrontLeft, filtered.FrontRight);
        var rear = Mean(filtered.RearLeft, filtered.RearRight);
        if (front < _settings.MinimumLearningSpeed || rear < _settings.MinimumLearningSpeed)
        {
            return;
        }

        var sample = Clamp(front / rear, 0.5f, 1.5f);
        _frontRearRatio = _hasFrontRearRatio
            ? Lerp(_frontRearRatio, sample, Clamp(_settings.BaselineLearningRate, 0f, 1f))
            : sample;
        _hasFrontRearRatio = true;
    }

    private void LearnLocalBaseline(WheelValues<float> excess)
    {
        var weight = Clamp(_settings.LocalBaselineLearningRate, 0f, 1f);
        var values = excess.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            _localBaseline[i] = Lerp(_localBaseline[i], values[i], weight);
        }
    }

    private WheelValues<float> Normalize(WheelValues<float> filtered)
    {
        var ratio = MathF.Max(_frontRearRatio, 0.001f);
        return new WheelValues<float>(
            filtered.FrontLeft / ratio,
            filtered.FrontRight / ratio,
            filtered.RearLeft,
            filtered.RearRight);
    }

    private static WheelMetrics CalculateMetrics(int drivetrain, WheelValues<float> normalized)
    {
        var front = Mean(normalized.FrontLeft, normalized.FrontRight);
        var rear = Mean(normalized.RearLeft, normalized.RearRight);
        var wheelExcess = drivetrain switch
        {
            0 => RelativeToReference(normalized, rear),
            1 => RelativeToReference(normalized, front),
            _ => RelativeToReference(normalized, Median(normalized)),
        };
        var drivenExcess = drivetrain switch
        {
            0 => Relative(front, rear),
            1 => Relative(rear, front),
            _ => MathF.Max(
                MathF.Max(0f, wheelExcess.FrontLeft),
                MathF.Max(
                    MathF.Max(0f, wheelExcess.FrontRight),
                    MathF.Max(MathF.Max(0f, wheelExcess.RearLeft), MathF.Max(0f, wheelExcess.RearRight)))),
        };
        return new WheelMetrics(
            wheelExcess,
            drivenExcess,
            Relative(front, rear),
            Relative(normalized.FrontLeft, normalized.FrontRight),
            Relative(normalized.RearLeft, normalized.RearRight));
    }

    private WheelMetrics ApplyLocalBaseline(int drivetrain, WheelMetrics metrics)
    {
        if (drivetrain != 2)
        {
            return metrics;
        }

        var anomaly = metrics.WheelExcess.ToArray()
            .Select((value, index) => value - _localBaseline[index])
            .ToArray();
        var drivenExcess = anomaly.Max(value => MathF.Max(0f, value));
        return metrics with { DrivenExcess = MathF.Max(drivenExcess, MathF.Abs(metrics.FrontRearDifference)) };
    }

    private WheelValues<bool> AffectedWheels(int drivetrain, WheelValues<float> excess)
    {
        excess = IndividualAnomaly(excess);
        var threshold = _settings.MinimumIndividualExcess;
        return drivetrain switch
        {
            0 => new WheelValues<bool>(
                excess.FrontLeft >= threshold, excess.FrontRight >= threshold, false, false),
            1 => new WheelValues<bool>(
                false, false, excess.RearLeft >= threshold, excess.RearRight >= threshold),
            _ => new WheelValues<bool>(
                excess.FrontLeft >= threshold,
                excess.FrontRight >= threshold,
                excess.RearLeft >= threshold,
                excess.RearRight >= threshold),
        };
    }

    private WheelSpeedAxle AffectedAxle(int drivetrain, WheelValues<float> excess)
    {
        excess = IndividualAnomaly(excess);
        var threshold = _settings.MinimumIndividualExcess;
        var front = excess.FrontLeft >= threshold || excess.FrontRight >= threshold;
        var rear = excess.RearLeft >= threshold || excess.RearRight >= threshold;
        return drivetrain switch
        {
            0 when front => WheelSpeedAxle.Front,
            1 when rear => WheelSpeedAxle.Rear,
            2 when front && rear => WheelSpeedAxle.Both,
            2 when front => WheelSpeedAxle.Front,
            2 when rear => WheelSpeedAxle.Rear,
            _ => WheelSpeedAxle.Unknown,
        };
    }

    private static bool HasAffectedWheel(WheelValues<bool> wheels) =>
        wheels.FrontLeft || wheels.FrontRight || wheels.RearLeft || wheels.RearRight;

    private float CollapseConfidence(float peakDrop, float collapseRate, float derivative)
    {
        var amount = Clamp(peakDrop / MathF.Max(_settings.MinimumCollapse * 2f, 0.001f), 0f, 1f);
        var sharpness = Clamp(
            (collapseRate - _settings.MinimumCollapseRate)
            / MathF.Max(StrongCollapseRate - _settings.MinimumCollapseRate, 0.001f),
            0f,
            1f);
        var negativeSlope = Clamp(
            -derivative / MathF.Max(StrongCollapseRate, 0.001f),
            0f,
            1f);
        return (amount + sharpness + negativeSlope) / 3f;
    }

    private float CollapseRate(TimeSpan currentAt, float peakDrop, float derivative)
    {
        var elapsed = _peakAt > TimeSpan.Zero && currentAt > _peakAt
            ? (float)(currentAt - _peakAt).TotalSeconds
            : 0.016f;
        return MathF.Max(-derivative, peakDrop / MathF.Max(elapsed, 0.001f));
    }

    private float ReconvergenceConfidence(float drivenExcess) =>
        _peakExcess <= 0f
            ? 0f
            : Clamp(1f - drivenExcess / MathF.Max(_peakExcess, 0.001f), 0f, 1f);

    private float ModulationConfidence()
    {
        var required = Math.Max(2, MinimumModulationEvents);
        return Clamp((float)(_collapseEvents.Count - 1) / (required - 1), 0f, 1f);
    }

    private void TrimCollapseEvents(TimeSpan now)
    {
        if (now <= TimeSpan.Zero || _settings.ModulationWindowMilliseconds <= 0)
        {
            while (_collapseEvents.Count > 8)
            {
                _collapseEvents.RemoveAt(0);
            }

            return;
        }

        var earliest = now - TimeSpan.FromMilliseconds(_settings.ModulationWindowMilliseconds);
        _collapseEvents.RemoveAll(time => time > TimeSpan.Zero && time < earliest);
    }

    private bool WithinHold(TimeSpan now, TimeSpan startedAt, int samples, bool recovery = false)
    {
        var milliseconds = recovery ? _settings.RecoveryMilliseconds : _settings.InterventionHoldMilliseconds;
        var sampleLimit = recovery ? RecoverySamples : InterventionHoldSamples;
        if (sampleLimit > 0 && samples <= sampleLimit)
        {
            return true;
        }

        return milliseconds > 0
               && (startedAt <= TimeSpan.Zero || now <= TimeSpan.Zero || now - startedAt < TimeSpan.FromMilliseconds(milliseconds));
    }

    private static bool IsTelemetryGap(WheelFrame previous, TelemetrySnapshot current)
    {
        if (previous.Timestamp <= TimeSpan.Zero || current.ReceivedAt <= TimeSpan.Zero)
        {
            return false;
        }

        var delta = current.ReceivedAt - previous.Timestamp;
        return delta <= TimeSpan.Zero
               || delta.TotalMilliseconds > MaximumTelemetryGapMilliseconds;
    }

    private static bool IsImplausibleJump(WheelValues<float> previous, WheelValues<float> current)
    {
        var oldValues = previous.ToArray();
        var newValues = current.ToArray();
        for (var i = 0; i < oldValues.Length; i++)
        {
            var jump = MathF.Abs(newValues[i] - oldValues[i]);
            var allowed = MathF.Max(
                MaximumWheelSpeedJump,
                MathF.Abs(oldValues[i]) * MaximumWheelSpeedJumpFraction);
            if (jump > allowed)
            {
                return true;
            }
        }

        return false;
    }

    private WheelValues<float> Filter(WheelValues<float> current, WheelValues<float> previous)
    {
        var alpha = Clamp(_settings.FilterAlpha, 0f, 1f);
        return new WheelValues<float>(
            Lerp(previous.FrontLeft, current.FrontLeft, alpha),
            Lerp(previous.FrontRight, current.FrontRight, alpha),
            Lerp(previous.RearLeft, current.RearLeft, alpha),
            Lerp(previous.RearRight, current.RearRight, alpha));
    }

    private static float ElapsedSeconds(TimeSpan previous, TimeSpan current)
    {
        if (previous > TimeSpan.Zero && current > previous)
        {
            var elapsed = (float)(current - previous).TotalSeconds;
            if (elapsed * 1000f <= MaximumTelemetryGapMilliseconds)
            {
                return elapsed;
            }
        }

        return 0.016f;
    }

    private static WheelDerivatives Derivatives(
        WheelMetrics current,
        WheelMetrics previous,
        float seconds) => new(
            new WheelValues<float>(
                (current.WheelExcess.FrontLeft - previous.WheelExcess.FrontLeft) / seconds,
                (current.WheelExcess.FrontRight - previous.WheelExcess.FrontRight) / seconds,
                (current.WheelExcess.RearLeft - previous.WheelExcess.RearLeft) / seconds,
                (current.WheelExcess.RearRight - previous.WheelExcess.RearRight) / seconds),
            (current.DrivenExcess - previous.DrivenExcess) / seconds);

    private static WheelValues<float> RelativeToReference(WheelValues<float> wheels, float reference) =>
        new(
            Relative(wheels.FrontLeft, reference),
            Relative(wheels.FrontRight, reference),
            Relative(wheels.RearLeft, reference),
            Relative(wheels.RearRight, reference));

    private static float Relative(float value, float reference) =>
        (value - reference) / MathF.Max(MathF.Abs(reference), 0.001f);

    private WheelValues<float> IndividualAnomaly(WheelValues<float> excess) =>
        new(
            excess.FrontLeft - _localBaseline[0],
            excess.FrontRight - _localBaseline[1],
            excess.RearLeft - _localBaseline[2],
            excess.RearRight - _localBaseline[3]);

    private static float Mean(float left, float right) => (left + right) * 0.5f;

    private static float Median(WheelValues<float> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return (sorted[1] + sorted[2]) * 0.5f;
    }

    private static bool TryGetWheels(TelemetrySnapshot snapshot, out WheelValues<float> wheels)
    {
        var raw = snapshot.WheelRotationSpeed;
        var values = raw.ToArray();
        if (!snapshot.IsRaceOn || snapshot.DrivetrainType is < 0 or > 2
            || values.Any(value => !float.IsFinite(value) || MathF.Abs(value) > MaximumWheelSpeed))
        {
            wheels = default;
            return false;
        }

        wheels = new WheelValues<float>(
            MathF.Abs(raw.FrontLeft), MathF.Abs(raw.FrontRight),
            MathF.Abs(raw.RearLeft), MathF.Abs(raw.RearRight));
        return true;
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static float Clamp(float value, float minimum, float maximum) =>
        MathF.Max(minimum, MathF.Min(maximum, value));

    private readonly record struct WheelFrame(
        TimeSpan Timestamp,
        WheelValues<float> Raw,
        WheelValues<float> Filtered,
        WheelMetrics Metrics);

    private readonly record struct WheelMetrics(
        WheelValues<float> WheelExcess,
        float DrivenExcess,
        float FrontRearDifference,
        float FrontLeftRightDifference,
        float RearLeftRightDifference);

    private readonly record struct WheelDerivatives(
        WheelValues<float> WheelExcess,
        float DrivenExcess)
    {
        public static WheelDerivatives Zero => new(default, 0f);
    }
}

public sealed class WheelSpeedTcsEvidence
{
    public static WheelSpeedTcsEvidence Empty { get; } = new();

    public WheelValues<float> FilteredWheelSpeeds { get; init; }

    public WheelValues<float> ReferenceCorrectedWheelSpeeds { get; init; }

    public WheelValues<float> WheelExcess { get; init; }

    public WheelValues<float> WheelExcessDerivative { get; init; }

    public float DrivenExcess { get; init; }

    public float DrivenExcessDerivative { get; init; }

    public float FrontRearDifference { get; init; }

    public float FrontLeftRightDifference { get; init; }

    public float RearLeftRightDifference { get; init; }

    public float FrontRearBaselineRatio { get; init; } = 1f;

    public float RecentPeakExcess { get; init; }

    public float CollapseRate { get; init; }

    public float SpinConfidence { get; init; }

    public float InterventionConfidence { get; init; }

    public float ModulationConfidence { get; init; }

    public float ReconvergenceConfidence { get; init; }

    public float CombinedConfidence { get; init; }

    public WheelValues<bool> AffectedWheels { get; init; }

    public WheelSpeedAxle AffectedAxle { get; init; } = WheelSpeedAxle.Unknown;

    public WheelSpeedTcsState State { get; init; } = WheelSpeedTcsState.Normal;

    public bool Observable { get; init; }
}

public enum WheelSpeedTcsState
{
    Normal,
    SpinDeveloping,
    Spinning,
    InterventionCandidate,
    InterventionConfirmed,
    Recovery,
}

public enum WheelSpeedAxle
{
    Unknown,
    Front,
    Rear,
    Both,
}
