using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Applies persisted calibration and derives the useful powerband and shift RPMs from it.
/// </summary>
public sealed class PowerbandAnalyzer
{
    private int _binCount;

    private VehicleIdentity _identity;
    private bool _hasIdentity;
    private float _idleRpm;
    private float _maxRpm;
    private int _currentGear = -1;
    private VehicleCalibration? _appliedCalibration;
    private StoredPowerband? _appliedPowerband;
    private Dictionary<int, float> _appliedShiftRpms = [];
    private PowerbandState _state = PowerbandState.Empty;
    private float _stateEffectiveRedlinePercentile = 0.98f;

    public PowerbandAnalyzer(int binCount)
    {
        _binCount = Math.Clamp(binCount, 8, 256);
    }

    /// <summary>Current display state derived from persisted calibration.</summary>
    public PowerbandState State => _state;

    /// <summary>Applies a new curve resolution without discarding the current calibration.</summary>
    public void ApplyConfiguration(PowerbandSettings settings)
    {
        var binCount = Math.Clamp(settings.BinCount, 8, 256);
        if (binCount == _binCount)
        {
            return;
        }

        var calibration = _appliedCalibration;
        _binCount = binCount;
        ClearCalibration();
        if (calibration is not null)
        {
            ApplyCalibration(calibration, settings);
        }
    }

    /// <summary>Updates the current vehicle identity and gear without learning live power data.</summary>
    public void UpdateVehicle(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!HasUsableVehicleFrame(snapshot))
        {
            return;
        }

        var identity = VehicleIdentity.From(snapshot);
        if (!_hasIdentity || identity != _identity || RpmScaleChanged(snapshot.EngineMaxRpm))
        {
            Reset(identity, snapshot.EngineIdleRpm, snapshot.EngineMaxRpm);
        }

        _currentGear = snapshot.Gear;
        if (_appliedPowerband is { } powerband)
        {
            _state = CreateState(
                powerband.PowerbandStartRpm,
                powerband.PowerbandEndRpm,
                powerband.PeakPowerRpm,
                _currentGear,
                _state.RpmDropRatio);
        }
    }

    /// <summary>Applies a persisted calibration and derives all display markers from its raw data.</summary>
    public void ApplyCalibration(VehicleCalibration calibration, PowerbandSettings settings)
    {
        if (!_hasIdentity || !IsSameVehicleIdentity(calibration.Identity, _identity) || _identity.IsElectric)
        {
            return;
        }

        if (!TryCalculateStoredPowerband(calibration.PowerCurve, settings, out var powerband))
        {
            return;
        }

        _appliedCalibration = calibration;
        _appliedPowerband = powerband;
        _stateEffectiveRedlinePercentile = settings.EffectiveRedlinePercentile;
        var storedCurve = CreateStoredPowerCurve(calibration.PowerCurve);
        var accelerationProfiles = ShiftAccelerationProfileBuilder.Build(
            calibration.RawPowerSamples,
            settings.MinimumShiftOptimizationSamplesPerGear);
        _appliedShiftRpms = CalculateShiftRpms(
            calibration.ShiftUpRpmDropRatioByGear,
            settings,
            powerband.PeakPowerRpm,
            storedCurve,
            GetStoredMaxPracticalRpm(calibration.PowerCurve, settings),
            accelerationProfiles,
            calibration.ShiftDurationMilliseconds);
        _state = CreateState(
            powerband.PowerbandStartRpm,
            powerband.PowerbandEndRpm,
            powerband.PeakPowerRpm,
            _currentGear,
            _state.RpmDropRatio);
    }

    public void ClearCalibration()
    {
        _appliedCalibration = null;
        _appliedPowerband = null;
        _appliedShiftRpms = new Dictionary<int, float>();
        _state = PowerbandState.Empty;
    }

    private void Reset(VehicleIdentity identity, float idleRpm, float maxRpm)
    {
        _identity = identity;
        _hasIdentity = true;
        _idleRpm = idleRpm;
        _maxRpm = maxRpm;
        _currentGear = -1;
        _appliedCalibration = null;
        _appliedPowerband = null;
        _appliedShiftRpms = new Dictionary<int, float>();
        _state = PowerbandState.Empty;
    }

    private bool RpmScaleChanged(float maxRpm) =>
        _maxRpm <= 0 || Math.Abs(maxRpm - _maxRpm) / _maxRpm > 0.01f;

    private static bool IsSameVehicleIdentity(VehicleIdentity left, VehicleIdentity right) =>
        left.CarOrdinal == right.CarOrdinal
        && left.CarPerformanceIndex == right.CarPerformanceIndex
        && left.DrivetrainType == right.DrivetrainType
        && left.NumCylinders == right.NumCylinders
        && MathF.Round(left.MaxRpm, MidpointRounding.AwayFromZero)
            == MathF.Round(right.MaxRpm, MidpointRounding.AwayFromZero);

    private static bool HasUsableVehicleFrame(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && snapshot.EngineMaxRpm > 0
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm;

    private PowerbandState CreateState(
        float start,
        float end,
        float peak,
        int currentGear,
        float previousDropRatio)
    {
        var effectiveRedline = EffectiveRedline(_maxRpm, _stateEffectiveRedlinePercentile);
        var shiftRpm = _appliedShiftRpms.TryGetValue(currentGear, out var currentShift)
            ? currentShift
            : effectiveRedline;

        return new PowerbandState(
            IsLearned: true,
            peak,
            start,
            end,
            shiftRpm,
            previousDropRatio)
        {
            ShiftRpms = _appliedShiftRpms,
        };
    }

    private static bool TryCalculateStoredPowerband(
        IReadOnlyList<PowerCurvePoint> curve,
        PowerbandSettings settings,
        out StoredPowerband powerband)
    {
        var points = curve
            .Where(point => point.Rpm > 0f && point.Power > 0f)
            .OrderBy(point => point.Rpm)
            .ToArray();
        if (points.Length == 0)
        {
            powerband = default;
            return false;
        }

        var peakLimit = PointLimitIndex(points.Length, settings.PeakPowerTopPercentile);
        var bandLimit = PointLimitIndex(points.Length, settings.PowerbandTopPercentile);
        var peakIndex = 0;
        for (var i = 1; i <= peakLimit; i++)
        {
            if (points[i].Power > points[peakIndex].Power)
            {
                peakIndex = i;
            }
        }

        var peakPower = points[peakIndex].Power;
        var threshold = peakPower * settings.PowerbandFraction;
        var bandPeakIndex = Math.Min(peakIndex, bandLimit);
        var startIndex = bandPeakIndex;
        while (startIndex > 0 && points[startIndex - 1].Power >= threshold)
        {
            startIndex--;
        }

        var endIndex = bandPeakIndex;
        while (endIndex < bandLimit && points[endIndex + 1].Power >= threshold)
        {
            endIndex++;
        }

        powerband = new StoredPowerband(
            points[startIndex].Rpm,
            points[endIndex].Rpm,
            points[peakIndex].Rpm);
        return true;
    }

    private GearPowerCurve CreateStoredPowerCurve(IReadOnlyList<PowerCurvePoint> curve)
    {
        var stored = new GearPowerCurve(_binCount);
        foreach (var point in curve)
        {
            var bin = BinFor(point.Rpm);
            if (bin < 0 || point.Power <= 0f)
            {
                continue;
            }

            stored.Power[bin] = Math.Max(stored.Power[bin], point.Power);
            stored.SampleCount[bin] = 1;
        }

        Smooth(stored.Power, stored.SampleCount, stored.SmoothedPower);
        return stored;
    }

    private static float GetStoredMaxPracticalRpm(
        IReadOnlyList<PowerCurvePoint> curve,
        PowerbandSettings settings)
    {
        var points = curve
            .Where(point => point.Rpm > 0f && point.Power > 0f)
            .OrderBy(point => point.Rpm)
            .ToArray();
        var topCount = Math.Max(
            1,
            (int)MathF.Ceiling(points.Length * Math.Clamp(settings.PracticalRedlineTopPercentile, 0.001f, 1f)));
        var start = points.Length - topCount;
        return points.Skip(start).Average(point => point.Rpm);
    }

    private static int PointLimitIndex(int pointCount, float topPercentile)
    {
        var topCount = (int)MathF.Floor(pointCount * Math.Clamp(topPercentile, 0f, 1f));
        return Math.Max(0, pointCount - topCount - 1);
    }

    private Dictionary<int, float> CalculateShiftRpms(
        IReadOnlyDictionary<int, List<float>> shiftDropRatios,
        PowerbandSettings settings,
        float peakPowerRpm,
        GearPowerCurve referenceCurve,
        float maxPracticalRpm,
        Dictionary<int, ShiftAccelerationProfile> accelerationProfiles,
        IReadOnlyList<float> shiftDurationsMilliseconds)
    {
        var result = new Dictionary<int, float>();
        var theoreticalRedline = EffectiveRedline(_maxRpm, settings.EffectiveRedlinePercentile);
        var effectiveRedline = Math.Min(theoreticalRedline, maxPracticalRpm);
        var shiftDuration = shiftDurationsMilliseconds
            .Where(float.IsFinite)
            .DefaultIfEmpty()
            .Average();

        foreach (var (gear, samples) in shiftDropRatios.OrderByDescending(pair => pair.Key))
        {
            var ratio = samples.Count == 0 ? 0f : samples.Average();
            if (ratio <= 0f || ratio >= 1f)
            {
                continue;
            }

            var nextShiftRpm = result.TryGetValue(gear + 1, out var nextShift)
                ? nextShift
                : effectiveRedline;
            var practicalShiftRpm = 0f;
            var hasPracticalOptimization = accelerationProfiles.TryGetValue(gear, out var currentProfile)
                && accelerationProfiles.TryGetValue(gear + 1, out var nextProfile)
                && ShiftPointOptimizer.TryFindOptimalShiftRpm(
                    peakPowerRpm,
                    effectiveRedline,
                    nextShiftRpm,
                    ratio,
                    currentProfile,
                    nextProfile,
                    shiftDuration,
                    settings.ShiftOptimizationCandidateStepRpm,
                    settings.ShiftOptimizationSpeedIntegrationSteps,
                    out practicalShiftRpm);
            var shiftRpm = hasPracticalOptimization
                ? practicalShiftRpm
                : FindCrossoverShiftRpm(referenceCurve, ratio, peakPowerRpm, effectiveRedline);
            result[gear] = Math.Min(shiftRpm, maxPracticalRpm);
        }

        return result;
    }

    private float FindCrossoverShiftRpm(
        GearPowerCurve referenceCurve,
        float ratio,
        float peakPowerRpm,
        float effectiveRedline)
    {
        if (ratio <= 0 || ratio >= 1f || peakPowerRpm <= 0)
        {
            return effectiveRedline;
        }

        var firstBin = Array.FindIndex(referenceCurve.SampleCount, count => count > 0);
        var lastBin = Array.FindLastIndex(referenceCurve.SampleCount, count => count > 0);
        if (firstBin < 0 || lastBin < 0)
        {
            return effectiveRedline;
        }

        var minimumRpm = RpmAt(firstBin);
        var maximumRpm = Math.Min(RpmAt(lastBin), effectiveRedline);
        if (maximumRpm < minimumRpm)
        {
            return effectiveRedline;
        }

        const int stepCount = 100;
        var startRpm = Math.Clamp(peakPowerRpm, minimumRpm, maximumRpm);
        var stepSize = (maximumRpm - startRpm) / stepCount;
        if (stepSize <= 0f)
        {
            return maximumRpm;
        }

        var prevRpm = startRpm;
        var prevPowerCurr = GetInterpolatedPower(referenceCurve, prevRpm, firstBin, lastBin);
        var prevPowerNext = GetInterpolatedPower(referenceCurve, prevRpm * ratio, firstBin, lastBin);
        var prevDiff = prevPowerCurr - prevPowerNext;

        if (prevDiff <= 0f)
        {
            return startRpm;
        }

        for (var step = 1; step <= stepCount; step++)
        {
            var currentRpm = startRpm + step * stepSize;
            var currentPowerCurr = GetInterpolatedPower(referenceCurve, currentRpm, firstBin, lastBin);
            var currentPowerNext = GetInterpolatedPower(referenceCurve, currentRpm * ratio, firstBin, lastBin);
            var currentDiff = currentPowerCurr - currentPowerNext;

            if (currentDiff <= 0f)
            {
                var denominator = prevDiff - currentDiff;
                var fraction = denominator <= 0f ? 0f : prevDiff / denominator;
                var shiftRpm = prevRpm + fraction * (currentRpm - prevRpm);
                return Math.Clamp(shiftRpm, minimumRpm, maximumRpm);
            }

            prevRpm = currentRpm;
            prevDiff = currentDiff;
        }

        return maximumRpm;
    }

    private float GetInterpolatedPower(GearPowerCurve curve, float rpm, int firstBin, int lastBin)
    {
        if (firstBin < 0 || lastBin < 0 || _maxRpm <= _idleRpm)
        {
            return 0f;
        }

        var minRpm = RpmAt(firstBin);
        var maxRpm = RpmAt(lastBin);
        if (rpm <= minRpm)
        {
            return curve.SmoothedPower[firstBin];
        }

        if (rpm >= maxRpm)
        {
            return curve.SmoothedPower[lastBin];
        }

        var bin = BinFor(rpm);
        var lowerBin = Math.Clamp(bin, firstBin, lastBin);
        while (lowerBin > firstBin && curve.SampleCount[lowerBin] == 0)
        {
            lowerBin--;
        }

        var upperBin = Math.Clamp(bin + 1, firstBin, lastBin);
        while (upperBin < lastBin && curve.SampleCount[upperBin] == 0)
        {
            upperBin++;
        }

        if (lowerBin == upperBin || RpmAt(upperBin) <= RpmAt(lowerBin))
        {
            return curve.SmoothedPower[lowerBin];
        }

        var fraction = (rpm - RpmAt(lowerBin)) / (RpmAt(upperBin) - RpmAt(lowerBin));
        return curve.SmoothedPower[lowerBin] + fraction * (curve.SmoothedPower[upperBin] - curve.SmoothedPower[lowerBin]);
    }

    private int BinFor(float rpm)
    {
        if (_maxRpm <= _idleRpm)
        {
            return -1;
        }

        var bin = (int)((rpm - _idleRpm) / (_maxRpm - _idleRpm) * _binCount);
        return Math.Clamp(bin, 0, _binCount - 1);
    }

    private float RpmAt(int bin) =>
        _idleRpm + ((bin + 0.5f) / _binCount) * (_maxRpm - _idleRpm);

    private static float EffectiveRedline(float maxRpm, float percentile) =>
        maxRpm * Math.Clamp(percentile, 0f, 1f);

    private static void Smooth(
        ReadOnlySpan<float> source,
        ReadOnlySpan<int> sampleCount,
        Span<float> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (sampleCount[i] == 0)
            {
                destination[i] = 0f;
                continue;
            }

            var sum = source[i] * 2f;
            var weight = 2f;
            if (i > 0 && sampleCount[i - 1] > 0)
            {
                sum += source[i - 1];
                weight += 1f;
            }

            if (i < source.Length - 1 && sampleCount[i + 1] > 0)
            {
                sum += source[i + 1];
                weight += 1f;
            }

            destination[i] = sum / weight;
        }
    }

    private sealed class GearPowerCurve
    {
        public GearPowerCurve(int binCount)
        {
            Power = new float[binCount];
            SmoothedPower = new float[binCount];
            SampleCount = new int[binCount];
        }

        public float[] Power { get; }

        public float[] SmoothedPower { get; }

        public int[] SampleCount { get; }
    }

    private readonly record struct StoredPowerband(
        float PowerbandStartRpm,
        float PowerbandEndRpm,
        float PeakPowerRpm);
}

/// <summary>The useful powerband derived from persisted calibration.</summary>
public sealed record PowerbandState(
    bool IsLearned,
    float PeakPowerRpm,
    float PowerbandStartRpm,
    float PowerbandEndRpm,
    float ShiftRpm,
    float RpmDropRatio)
{
    /// <summary>Per-gear optimal shift values, derived from stored gear performance data.</summary>
    public IReadOnlyDictionary<int, float> ShiftRpms { get; init; } = new Dictionary<int, float>();

    /// <summary>State before persisted calibration is available.</summary>
    public static PowerbandState Empty { get; } = new(false, 0f, 0f, 0f, 0f, 0f);

}
