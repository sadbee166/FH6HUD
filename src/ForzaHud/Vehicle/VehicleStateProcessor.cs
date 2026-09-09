using System.Globalization;
using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Turns raw telemetry into values the HUD can draw.
///
/// All derived calculations live here or in the dedicated analysis classes next to it -
/// never inside a HUD element. This keeps the algorithms independently testable against
/// recorded sessions.
/// </summary>
public sealed class VehicleStateProcessor
{
    private const int CalibrationProgressSampleInterval = 30;

    private readonly PowerbandAnalyzer _powerband;
    private readonly TractionControlAnalyzer _tractionControl;
    private readonly HudConfiguration _configuration;
    private readonly CalibrationDataStore _calibrationStore;
    private readonly Action<string>? _calibrationOutput;
    private readonly object _calibrationGate = new();

    private float _peakBoostPsi;
    private RpmRange _lastValidRpmRange;
    private VehicleIdentity? _currentIdentity;
    private CalibrationRecorder? _calibrationRecorder;
    private ShiftUpRpmDropRatioLogger? _liveShiftLogger;
    private TelemetrySnapshot? _lastSnapshot;
    private int _lastCalibrationProgressSamples;
    private bool _calibrationInvalidationReported;
    private LiveCalibrationState _liveState = LiveCalibrationState.Identifying;
    private VehicleCalibration? _liveMatchedCalibration;
    private bool _powerCollectionFinished;
    private bool _angularVelocityInitialized;
    private bool _previousAngularVelocityFrameWasDriving;
    private TimeSpan _previousAngularVelocityAt;
    private float _previousAngularVelocityX;
    private float _previousAngularVelocityY;
    private bool _powerSampleAffectedByTcs;

    public VehicleStateProcessor(
        HudConfiguration configuration,
        CalibrationDataStore? calibrationStore = null,
        Action<string>? calibrationOutput = null)
    {
        _configuration = configuration;
        _powerband = new PowerbandAnalyzer(configuration.Telemetry.Powerband.BinCount);
        _tractionControl = new TractionControlAnalyzer(configuration.Telemetry.TractionControl);
        _calibrationStore = calibrationStore ?? new CalibrationDataStore(
            Path.Combine(
                AppContext.BaseDirectory,
                configuration.Calibration.DataFile),
            carOrdinalNamesFilePath: Path.Combine(
                AppContext.BaseDirectory,
                configuration.Calibration.CarOrdinalNamesFile),
            rawSamplesDirectory: Path.Combine(
                AppContext.BaseDirectory,
                configuration.Calibration.RawSamplesDirectory));
        _calibrationOutput = calibrationOutput;
    }

    /// <summary>Learned powerband state for the current car.</summary>
    public PowerbandState Powerband
    {
        get
        {
            lock (_calibrationGate)
            {
                return DisplayPowerband();
            }
        }
    }

    /// <summary>Whether calibration recorder mode is currently active.</summary>
    public bool IsCalibrationRecording
    {
        get
        {
            lock (_calibrationGate)
            {
                return _configuration.Calibration.Live.Enabled
                    ? _liveState == LiveCalibrationState.Calculating
                      && _calibrationRecorder?.IsRecording == true
                      && _lastSnapshot?.IsRaceOn == true
                    : _calibrationRecorder?.IsRecording == true;
            }
        }
    }

    /// <summary>Refreshes processor settings after a validated live configuration reload.</summary>
    internal void ApplyConfiguration() =>
        _powerband.ApplyConfiguration(_configuration.Telemetry.Powerband);

    /// <summary>Toggles calibration mode and saves a valid completed record.</summary>
    public CalibrationToggleResult ToggleCalibrationRecording()
    {
        lock (_calibrationGate)
        {
            if (_configuration.Calibration.Live.Enabled)
            {
                return new CalibrationToggleResult(IsRecording: IsCalibrationRecording, Saved: false);
            }

            if (_currentIdentity?.IsElectric == true)
            {
                LogCalibration("Cannot calibrate RPM: the current vehicle is an EV.");
                return new CalibrationToggleResult(IsRecording: false, Saved: false);
            }

            if (_calibrationRecorder is null)
            {
                _calibrationRecorder = new CalibrationRecorder(
                    _configuration.Telemetry.Powerband,
                    _configuration.Telemetry.TractionControl);
                _calibrationRecorder.Start(
                    _lastSnapshot,
                    includeInitialPowerSample: !_powerSampleAffectedByTcs);
                _lastCalibrationProgressSamples = 0;
                _calibrationInvalidationReported = false;
                LogCalibrationStart(_calibrationRecorder);
                return new CalibrationToggleResult(IsRecording: true, Saved: false);
            }

            var recorder = _calibrationRecorder;
            _calibrationRecorder = null;
            var result = FinishCalibration(recorder);
            ResetCalibrationOutputState();
            return result;
        }
    }

    /// <summary>Deletes all persisted calibration records for the currently identified vehicle.</summary>
    public bool TryDeleteCurrentCalibration()
    {
        lock (_calibrationGate)
        {
            if (_currentIdentity is not { } identity)
            {
                LogCalibration("Cannot delete calibration: the current vehicle identity is unknown.");
                return false;
            }

            if (!_calibrationStore.TryDelete(identity))
            {
                LogCalibration($"No calibration record found for {FormatIdentity(identity)}.");
                return false;
            }

            if (_configuration.Calibration.Live.Enabled)
            {
                _liveMatchedCalibration = null;
                _powerband.ClearCalibration();
                ResetLiveVehicle(identity);
            }

            LogCalibration(
                $"Deleted calibration records for {FormatIdentity(identity)} from "
                + $"'{_calibrationStore.FilePath}'.");
            return true;
        }
    }

    /// <summary>Finishes an active calibration when telemetry is stopping.</summary>
    public CalibrationToggleResult FinishCalibrationRecording()
    {
        lock (_calibrationGate)
        {
            if (_configuration.Calibration.Live.Enabled)
            {
                if (_liveState is not (LiveCalibrationState.Calculating or LiveCalibrationState.Monitoring))
                {
                    return new CalibrationToggleResult(IsRecording: false, Saved: false);
                }

                var wasRecording = _calibrationRecorder is not null;
                FinishLiveSession();
                return new CalibrationToggleResult(IsRecording: wasRecording, Saved: false);
            }

            if (_calibrationRecorder is null)
            {
                return new CalibrationToggleResult(IsRecording: false, Saved: false);
            }

            var recorder = _calibrationRecorder;
            _calibrationRecorder = null;
            var result = FinishCalibration(recorder);
            ResetCalibrationOutputState();
            return result;
        }
    }

    /// <summary>Derives display-ready state from one telemetry frame.</summary>
    public DerivedState Process(TelemetrySnapshot snapshot, bool frameTcsActive = false)
    {
        var wasDriving = _lastSnapshot?.IsRaceOn == true;
        var drivingEntered = snapshot.IsRaceOn && !wasDriving;
        var drivingExited = wasDriving && !snapshot.IsRaceOn;
        var identityChanged = false;
        if (IsUsableVehicleFrame(snapshot))
        {
            var identity = VehicleIdentity.From(snapshot);
            identityChanged = _currentIdentity is null || _currentIdentity.Value != identity;
            if (identityChanged)
            {
                _currentIdentity = identity;
                _peakBoostPsi = 0f;
                _lastValidRpmRange = default;
            }
        }

        if (identityChanged)
        {
            _angularVelocityInitialized = false;
        }

        _powerband.UpdateVehicle(snapshot);

        var usesFrameTcs = _configuration.Telemetry.TractionControl.DetectionMode == TcsDetectionMode.Frame;
        var telemetryTractionControlActive = usesFrameTcs ? false : _tractionControl.Update(snapshot);
        var tractionControlActive = telemetryTractionControlActive;
        var powerSampleAffectedByTcs = usesFrameTcs
            ? frameTcsActive
            : _tractionControl.PowerSampleAffectedByTcs;

        PowerbandState displayPowerband;
        lock (_calibrationGate)
        {
            _powerSampleAffectedByTcs = powerSampleAffectedByTcs;
            if (_configuration.Calibration.Live.Enabled)
            {
                if (identityChanged)
                {
                    ResetLiveVehicle(_currentIdentity!.Value);
                }

                if (drivingEntered && !identityChanged && _currentIdentity is { } identity)
                {
                    BeginLiveVehicle(identity);
                    if (_liveState == LiveCalibrationState.Calculating)
                    {
                        _liveShiftLogger = new ShiftUpRpmDropRatioLogger();
                    }
                }

                if (drivingExited)
                {
                    FinishLiveSession();
                }
                else if (snapshot.IsRaceOn)
                {
                    ProcessLiveCalibration(snapshot);
                }

                displayPowerband = DisplayPowerband();
            }
            else
            {
                if (identityChanged && _currentIdentity is { } identity
                    && !identity.IsElectric)
                {
                    var exact = _calibrationStore.FindExact(identity);
                    if (exact.Count > 0)
                    {
                        _powerband.ApplyCalibration(exact[0], _configuration.Telemetry.Powerband);
                    }
                }

                if (_calibrationRecorder is { } recorder)
                {
                    var observedShift = recorder.Record(
                        snapshot,
                        includePowerSample: !_powerSampleAffectedByTcs);
                    if (observedShift is { } shift)
                    {
                        LogShiftRatioObserved(shift);
                    }
                    LogCalibrationProgress(recorder, snapshot);
                }

                displayPowerband = _powerband.State;
            }

            _lastSnapshot = snapshot;
        }

        var units = _configuration.Units;
        var reportedRange = new RpmRange(snapshot.EngineIdleRpm, snapshot.EngineMaxRpm);
        if (snapshot.IsRaceOn && reportedRange.IsValid)
        {
            _lastValidRpmRange = reportedRange;
        }

        var range = snapshot.IsRaceOn && reportedRange.IsValid
            ? reportedRange
            : _lastValidRpmRange;

        _peakBoostPsi = Math.Max(_peakBoostPsi, snapshot.Boost);
        var hasBoost = _peakBoostPsi >= BoostDeadbandPsi;

        var grip = GripAnalyzer.Analyze(snapshot, _configuration.Telemetry.Grip);
        var worst = GripAnalyzer.Worst(grip);
        var angularAcceleration = CalculateAngularAcceleration(snapshot);

        return new DerivedState
        {
            IsDriving = snapshot.IsRaceOn,
            IsElectric = _currentIdentity?.IsElectric ?? (snapshot.NumCylinders == 0),
            Timestamp = snapshot.ReceivedAt,
            SpeedMetersPerSecond = snapshot.Speed,
            DisplaySpeed = SpeedConverter.Convert(snapshot.Speed, units.Speed),
            SpeedUnitLabel = SpeedConverter.UnitLabel(units.Speed),
            Rpm = snapshot.CurrentEngineRpm,
            RpmRange = range,
            RpmFraction = range.IsValid
                ? Common.MathHelper.Clamp01(snapshot.CurrentEngineRpm / range.MaxRpm)
                : 0f,
            RpmNormalized = range.IsValid
                ? Common.MathHelper.Clamp01((snapshot.CurrentEngineRpm - range.IdleRpm) / (range.MaxRpm - range.IdleRpm))
                : 0f,
            Gear = snapshot.Gear,
            GearLabel = GearLabel(snapshot.Gear, snapshot.IsRaceOn),
            BoostPsi = Math.Abs(snapshot.Boost) >= BoostDeadbandPsi ? snapshot.Boost : 0f,
            HasBoost = hasBoost,
            Throttle = snapshot.Throttle,
            Brake = snapshot.Brake,
            Steer = snapshot.Steer,
            RollDegrees = snapshot.Roll * (180f / MathF.PI),
            LateralG = GForceCalculator.Lateral(snapshot.AccelerationX, _configuration.Telemetry.GForce),
            LongitudinalG = GForceCalculator.Longitudinal(snapshot.AccelerationZ, _configuration.Telemetry.GForce),
            VerticalG = GForceCalculator.Vertical(snapshot.AccelerationY, _configuration.Telemetry.GForce),
            PitchAngularAcceleration = angularAcceleration.Pitch,
            YawAngularAcceleration = angularAcceleration.Yaw,
            Powerband = displayPowerband,
            TractionControlActive = tractionControlActive,
            WheelSpeedTcsEvidence = _tractionControl.WheelSpeedEvidence,
            TireGrip = grip,
            WorstTireGrip = worst,
            HasGripLoss = GripAnalyzer.IsGripLoss(worst),
        };
    }

    private (float Pitch, float Yaw) CalculateAngularAcceleration(TelemetrySnapshot snapshot)
    {
        var pitch = 0f;
        var yaw = 0f;

        if (_angularVelocityInitialized
            && _previousAngularVelocityFrameWasDriving
            && snapshot.IsRaceOn
            && snapshot.ReceivedAt > _previousAngularVelocityAt)
        {
            var deltaSeconds = (float)(snapshot.ReceivedAt - _previousAngularVelocityAt).TotalSeconds;
            pitch = (snapshot.AngularVelocityX - _previousAngularVelocityX) / deltaSeconds;
            yaw = (snapshot.AngularVelocityY - _previousAngularVelocityY) / deltaSeconds;
        }

        _previousAngularVelocityX = snapshot.AngularVelocityX;
        _previousAngularVelocityY = snapshot.AngularVelocityY;
        _previousAngularVelocityAt = snapshot.ReceivedAt;
        _previousAngularVelocityFrameWasDriving = snapshot.IsRaceOn;
        _angularVelocityInitialized = true;

        return (pitch, yaw);
    }

    private void ResetLiveVehicle(VehicleIdentity identity)
    {
        _calibrationRecorder = null;
        _liveShiftLogger = null;
        _powerCollectionFinished = false;
        _liveMatchedCalibration = null;
        ResetCalibrationOutputState();

        if (identity.IsElectric)
        {
            _liveState = LiveCalibrationState.Waiting;
            LogCalibration($"Vehicle {FormatIdentity(identity)} is an EV; RPM calibration is disabled.");
            return;
        }

        BeginLiveVehicle(identity);
    }

    private void BeginLiveVehicle(VehicleIdentity identity)
    {
        var exact = _calibrationStore.FindExact(identity);
        var stored = exact.Count > 0 ? exact[0] : null;
        _liveMatchedCalibration = stored;

        if (stored?.PowerCurve is { Count: > 0 })
        {
            _powerband.ApplyCalibration(stored, _configuration.Telemetry.Powerband);
        }

        if (stored is not null)
        {
            LogCalibration($"Loaded {FormatIdentity(identity)} from the calibration database; "
                           + $"ShiftUpRpmDropRatioByGear: {FormatShiftRatios(stored.ShiftUpRpmDropRatioByGear)}.");
        }

        if (stored is not null
            && stored.PowerCurve.Count > 0
            && !_configuration.Calibration.Live.AllowOverwrite)
        {
            _liveState = LiveCalibrationState.Monitoring;
            _calibrationRecorder = null;
            _liveShiftLogger = new ShiftUpRpmDropRatioLogger();
            LogCalibration("Live power-curve overwrite is disabled; retaining the persisted curve and collecting shift ratios.");
            return;
        }

        _calibrationRecorder ??= new CalibrationRecorder(
            _configuration.Telemetry.Powerband,
            _configuration.Telemetry.TractionControl,
            stored?.RawPowerSamples);
        if (!_calibrationRecorder.IsRecording)
        {
            _calibrationRecorder.Start(initialSnapshot: null);
        }

        _liveShiftLogger ??= new ShiftUpRpmDropRatioLogger();
        _liveState = LiveCalibrationState.Calculating;
        LogCalibration($"Collecting live calibration for {FormatIdentity(identity)}.");
    }

    private void ProcessLiveCalibration(TelemetrySnapshot snapshot)
    {
        if (_currentIdentity is not { } identity || identity.IsElectric)
        {
            return;
        }

        if (_liveState == LiveCalibrationState.Identifying)
        {
            BeginLiveVehicle(identity);
        }

        if (_liveState is not (LiveCalibrationState.Calculating or LiveCalibrationState.Monitoring))
        {
            return;
        }

        var shiftLogger = _liveShiftLogger ??= new ShiftUpRpmDropRatioLogger();
        if (shiftLogger.Observe(snapshot) is { } observedShift)
        {
            var pendingSamples = shiftLogger.Ratios[observedShift.OutgoingGear].Count;
            LogShiftRatioObserved(observedShift, pendingSamples);
        }

        if (_liveState == LiveCalibrationState.Calculating
            && _calibrationRecorder is { } recorder)
        {
            recorder.Record(
                snapshot,
                includePowerSample: !_powerSampleAffectedByTcs && !_powerCollectionFinished);
            LogCalibrationProgress(recorder, snapshot);

            if (!_powerCollectionFinished && IsPowerCollectionComplete(recorder))
            {
                TryPersistLivePowerCurve(recorder);
            }
        }

        TryPersistLiveShiftRatios(shiftLogger);
    }

    private void FinishLiveSession()
    {
        if (_liveState is not (LiveCalibrationState.Calculating or LiveCalibrationState.Monitoring))
        {
            return;
        }

        if (_liveState == LiveCalibrationState.Calculating
            && _calibrationRecorder is { } recorder)
        {
            if (!_powerCollectionFinished && IsPowerCollectionComplete(recorder))
            {
                TryPersistLivePowerCurve(recorder);
            }

            recorder.ResetTemporalState();
        }

        if (_liveShiftLogger is { } shiftLogger)
        {
            shiftLogger.FlushPending();
            TryPersistLiveShiftRatios(shiftLogger, flushIncomplete: true);
            _liveShiftLogger = null;
        }

        if (_liveState == LiveCalibrationState.Calculating && _powerCollectionFinished)
        {
            _calibrationRecorder = null;
            _liveState = _liveMatchedCalibration?.PowerCurve.Count > 0
                ? LiveCalibrationState.Monitoring
                : LiveCalibrationState.Waiting;
        }
    }

    private bool IsPowerCollectionComplete(CalibrationRecorder recorder)
    {
        var analysis = recorder.GetCalibrationAnalysis();
        return analysis.AcceptedPowerSamples >= _configuration.Calibration.Live.PowerCurve.StopSampleCount
            && analysis.RpmCoverageFraction >= _configuration.Calibration.Live.PowerCurve.StopRpmCoverageFraction;
    }

    private bool TryPersistLivePowerCurve(CalibrationRecorder recorder)
    {
        if (_powerCollectionFinished
            || _currentIdentity is not { } identity
            || !recorder.TryGetPowerCurve(out var liveCurve))
        {
            return false;
        }

        var existing = _liveMatchedCalibration;
        var shouldWrite = existing?.PowerCurve is not { Count: > 0 };
        if (!shouldWrite)
        {
            if (!PowerCurveShapeComparer.TryGetShapeError(existing!.PowerCurve, liveCurve, out var shapeError))
            {
                shouldWrite = true;
            }
            else if (shapeError <= _configuration.Calibration.Live.PowerCurve.OverwriteShapeErrorThreshold)
            {
                _powerCollectionFinished = true;
                LogCalibration($"Skipped live power curve for {FormatIdentity(identity)}; overlap shape error "
                               + $"{shapeError.ToString("0.###", CultureInfo.InvariantCulture)} is within threshold.");
                return true;
            }
        }

        var shiftRatios = existing?.ShiftUpRpmDropRatioByGear.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList()) ?? [];
        if (_liveShiftLogger is { } shiftLogger)
        {
            foreach (var (gear, samples) in shiftLogger.Ratios)
            {
                if (samples.Count > 0)
                {
                    shiftRatios[gear] = [samples.Average()];
                }
            }
        }

        var calibration = new VehicleCalibration(
            identity,
            liveCurve,
            shiftRatios)
        {
            CarName = existing?.CarName,
            RawPowerSamples = recorder.RawPowerSamples.ToList(),
            ShiftDurationMilliseconds = existing?.ShiftDurationMilliseconds.ToList() ?? [],
        };
        if (_liveShiftLogger is { } liveShiftLogger)
        {
            if (liveShiftLogger.DurationMilliseconds.Count > 0)
            {
                calibration = calibration with
                {
                    ShiftDurationMilliseconds = liveShiftLogger.DurationMilliseconds.ToList(),
                };
            }
        }
        if (!_calibrationStore.TrySave(calibration))
        {
            LogCalibration($"FAILED: live power curve could not be saved to '{_calibrationStore.FilePath}'.");
            return false;
        }

        _powerCollectionFinished = true;
        RefreshLiveCalibration(identity);

        LogCalibration($"Saved live power curve for {FormatIdentity(identity)} to '{_calibrationStore.FilePath}'.");
        LogCalibration($"ShiftUpRpmDropRatioByGear after live power-curve save: "
                       + $"{FormatShiftRatios(_liveMatchedCalibration?.ShiftUpRpmDropRatioByGear ?? new Dictionary<int, List<float>>())}.");
        return true;
    }

    private void RefreshLiveCalibration(VehicleIdentity identity)
    {
        var exact = _calibrationStore.FindExact(identity);
        _liveMatchedCalibration = exact.Count > 0 ? exact[0] : null;
        if (_liveMatchedCalibration is { PowerCurve.Count: > 0 } saved)
        {
            _powerband.ApplyCalibration(saved, _configuration.Telemetry.Powerband);
        }
    }

    private bool TryPersistLiveShiftRatios(
        ShiftUpRpmDropRatioLogger logger,
        bool flushIncomplete = false)
    {
        if (_currentIdentity is not { } identity)
        {
            return false;
        }

        var existing = _liveMatchedCalibration;
        var savedAny = false;
        foreach (var (gear, samples) in logger.Ratios.ToArray())
        {
            if (!flushIncomplete
                && samples.Count < _configuration.Calibration.Live.GearShift.MinimumSamplesPerGear)
            {
                continue;
            }

            var newAverage = samples.Average();
            if (existing?.ShiftUpRpmDropRatioByGear.TryGetValue(gear, out var oldSamples) == true
                && oldSamples.Count > 0)
            {
                var oldAverage = oldSamples.Average();
                var ratioError = Math.Abs(newAverage - oldAverage) / oldAverage;
                if (ratioError <= _configuration.Calibration.Live.GearShift.OverwriteRatioErrorThreshold)
                {
                    logger.RemoveGear(gear);
                    LogCalibration($"Skipped live gear {gear} ratio; relative error "
                                   + $"{ratioError.ToString("0.###", CultureInfo.InvariantCulture)} is within threshold.");
                    continue;
                }
            }

            var ratios = existing?.ShiftUpRpmDropRatioByGear.ToDictionary(
                             pair => pair.Key,
                             pair => pair.Value.ToList())
                         ?? [];
            var durations = existing?.ShiftDurationMilliseconds.ToList() ?? [];
            ratios[gear] = [newAverage];
            if (logger.DurationMilliseconds.Count > 0)
            {
                durations = logger.DurationMilliseconds.ToList();
            }

            var calibration = new VehicleCalibration(identity, existing?.PowerCurve.ToList() ?? [], ratios)
            {
                CarName = existing?.CarName,
                ShiftDurationMilliseconds = durations,
                RawPowerSamples = existing?.RawPowerSamples.ToList() ?? [],
            };
            if (!_calibrationStore.TrySave(calibration))
            {
                LogCalibration($"FAILED: live gear {gear} ratio could not be saved to '{_calibrationStore.FilePath}'.");
                continue;
            }

            logger.RemoveGear(gear);
            existing = calibration;
            _liveMatchedCalibration = existing;
            if (existing is { PowerCurve.Count: > 0 } saved)
            {
                _powerband.ApplyCalibration(saved, _configuration.Telemetry.Powerband);
            }

            savedAny = true;
            LogCalibration($"Saved live gear {gear} ratio for {FormatIdentity(identity)}; "
                           + $"ShiftUpRpmDropRatioByGear: "
                           + $"{FormatShiftRatios(existing?.ShiftUpRpmDropRatioByGear ?? new Dictionary<int, List<float>>())}.");
        }

        return savedAny;
    }

    private PowerbandState DisplayPowerband() => _powerband.State;

    private CalibrationToggleResult FinishCalibration(CalibrationRecorder recorder)
    {
        if (!recorder.TryFinish(out var calibration))
        {
            LogCalibration($"FAILED: {DescribeCalibrationFailure(recorder)}");
            return new CalibrationToggleResult(IsRecording: false, Saved: false);
        }

        if (!_calibrationStore.TrySave(calibration))
        {
            LogCalibration($"FAILED: calibration was computed but could not be saved to '{_calibrationStore.FilePath}'.");
            return new CalibrationToggleResult(IsRecording: false, Saved: false);
        }

        if (_currentIdentity == calibration.Identity)
        {
            _powerband.ApplyCalibration(calibration, _configuration.Telemetry.Powerband);
        }

        var powerband = _powerband.State;
        LogCalibration(
            $"Saved {FormatIdentity(calibration.Identity)} to '{_calibrationStore.FilePath}'. "
            + $"Powerband: {powerband.PowerbandStartRpm.ToString("0", CultureInfo.InvariantCulture)}-"
            + $"{powerband.PowerbandEndRpm.ToString("0", CultureInfo.InvariantCulture)} RPM; "
            + $"peak: {powerband.PeakPowerRpm.ToString("0", CultureInfo.InvariantCulture)} RPM; "
            + $"curve points: {calibration.PowerCurve.Count}; "
            + $"ShiftUpRpmDropRatioByGear: {FormatShiftRatios(calibration.ShiftUpRpmDropRatioByGear)}.");

        return new CalibrationToggleResult(IsRecording: false, Saved: true);
    }

    private void LogCalibrationStart(CalibrationRecorder recorder)
    {
        var minimumThrottle = (_configuration.Telemetry.Powerband.MinimumThrottle * 100f)
            .ToString("0", CultureInfo.InvariantCulture);
        var identity = recorder.Identity is { } value
            ? FormatIdentity(value)
            : "vehicle identity pending usable race telemetry";

        LogCalibration(
            $"Started for {identity}. Drive straight through each forward gear at "
            + $">={minimumThrottle}% throttle, then press the calibration hotkey again to finish.");
    }

    private void LogCalibrationProgress(CalibrationRecorder recorder, TelemetrySnapshot snapshot)
    {
        if (recorder.WasInvalidated && !_calibrationInvalidationReported)
        {
            _calibrationInvalidationReported = true;
            LogCalibration("WARNING: vehicle identity changed during this run; the calibration will not be saved.");
        }

        if (recorder.AcceptedPowerSamples < _lastCalibrationProgressSamples + CalibrationProgressSampleInterval)
        {
            return;
        }

        _lastCalibrationProgressSamples = recorder.AcceptedPowerSamples;
        var analysis = recorder.GetCalibrationAnalysis();
        var identity = recorder.Identity is { } value ? FormatIdentity(value) : "identity pending";

        LogCalibration(
            $"Progress: samples={recorder.AcceptedPowerSamples}; rejected={analysis.RejectedPowerSamples}; "
            + $"bins={analysis.PopulatedRpmBins}; reliable={analysis.ReliableRpmBins}; "
            + $"coverage={analysis.RpmCoverageFraction.ToString("0.##", CultureInfo.InvariantCulture)}; {identity}; "
            + $"gear={snapshot.Gear}; rpm={snapshot.CurrentEngineRpm.ToString("0", CultureInfo.InvariantCulture)}; "
            + $"power={snapshot.Power.ToString("0", CultureInfo.InvariantCulture)} W.");
    }

    private static string DescribeCalibrationFailure(CalibrationRecorder recorder)
    {
        if (recorder.WasInvalidated)
        {
            return "vehicle identity changed during recording";
        }

        if (recorder.Identity is null)
        {
            return "no usable race telemetry was received";
        }

        var analysis = recorder.GetCalibrationAnalysis();
        return $"calibration had no valid WOT samples for {FormatIdentity(recorder.Identity.Value)} "
            + $"(accepted={analysis.AcceptedPowerSamples}, rejected={analysis.RejectedPowerSamples}, "
            + $"bins={analysis.PopulatedRpmBins}, reliable={analysis.ReliableRpmBins}, "
            + $"coverage={analysis.RpmCoverageFraction.ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    private void ResetCalibrationOutputState()
    {
        _lastCalibrationProgressSamples = 0;
        _calibrationInvalidationReported = false;
    }

    private static List<PowerCurveSample> LimitRawPowerSamples(
        IReadOnlyList<PowerCurveSample> samples,
        int maximumSamples) =>
        samples
            .Distinct()
            .TakeLast(Math.Max(1, maximumSamples))
            .ToList();

    private static string FormatIdentity(VehicleIdentity identity) =>
        $"car={identity.CarOrdinal}, PI={identity.CarPerformanceIndex}, "
        + $"drivetrain={identity.DrivetrainType}, cylinders={identity.NumCylinders}, "
        + $"maxRPM={identity.MaxRpm.ToString("0", CultureInfo.InvariantCulture)}";

    private void LogShiftRatioObserved(
        ShiftUpRpmDropRatio shift,
        int? pendingSamples = null)
    {
        var pending = pendingSamples is { } count
            ? $"; pending={count}/{_configuration.Calibration.Live.GearShift.MinimumSamplesPerGear}"
            : string.Empty;
        LogCalibration(
            $"Observed shift {shift.OutgoingGear}->{shift.OutgoingGear + 1}: "
            + $"RPM {shift.BeforeShiftRpm.ToString("0", CultureInfo.InvariantCulture)} -> "
            + $"{shift.AfterShiftRpm.ToString("0", CultureInfo.InvariantCulture)}, "
            + $"drop ratio={shift.Ratio.ToString("0.###", CultureInfo.InvariantCulture)}{pending}.");
    }

    private static string FormatShiftRatios(Dictionary<int, List<float>> ratios) =>
        ratios.Count == 0
            ? "none"
            : string.Join(
                ", ",
                ratios
                    .OrderBy(pair => pair.Key)
                    .Select(pair =>
                        $"{pair.Key}->{pair.Key + 1}="
                        + pair.Value.Average().ToString("0.###", CultureInfo.InvariantCulture)));

    private void LogCalibration(string message)
    {
        if (_configuration.Calibration.VerboseOutput)
        {
            _calibrationOutput?.Invoke($"[RPM calibration] {message}");
        }
    }

    private static bool IsUsableVehicleFrame(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm
        && snapshot.EngineMaxRpm > 0;

    /// <summary>
    /// Boost below this magnitude is vacuum noise on naturally aspirated cars. It keeps the
    /// boost element from showing a permanently twitching gauge on cars that have no turbo.
    /// </summary>
    public const float BoostDeadbandPsi = 0.5f;

    private static string GearLabel(int gear, bool isDriving) => gear switch
    {
        0 when isDriving => "R",
        0 => "-",
        11 when isDriving => "N",
        11 => "-",
        _ => gear.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private enum LiveCalibrationState
    {
        Identifying,
        Monitoring,
        Waiting,
        Calculating,
    }
}

/// <summary>Result of changing calibration recorder mode.</summary>
public readonly record struct CalibrationToggleResult(bool IsRecording, bool Saved);

/// <summary>
/// Speed conversion. Telemetry speed is metres per second; the HUD shows whichever unit is
/// configured.
/// </summary>
public static class SpeedConverter
{
    private const float MetersPerSecondToKilometersPerHour = 3.6f;
    private const float MetersPerSecondToMilesPerHour = 2.23693629f;

    public static float Convert(float metersPerSecond, SpeedUnit unit) => unit switch
    {
        SpeedUnit.MilesPerHour => metersPerSecond * MetersPerSecondToMilesPerHour,
        _ => metersPerSecond * MetersPerSecondToKilometersPerHour,
    };

    public static string UnitLabel(SpeedUnit unit) => unit switch
    {
        SpeedUnit.MilesPerHour => "MPH",
        _ => "KM/H",
    };
}
