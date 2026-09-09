using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class LiveCalibrationTests
{
    [Fact]
    public void ConfigurationLoadsTheRevisedLiveCalibrationBlock()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, """
            {
              "calibration": {
                "dataFile": "shared.json",
                "carOrdinalNamesFile": "car-names.json",
                "live": {
                  "enabled": true,
                  "allowOverwrite": true,
                  "powerCurve": {
                    "stopSampleCount": 8,
                    "stopRpmCoverageFraction": 0.5,
                    "overwriteShapeErrorThreshold": 0.2
                  },
                  "gearShift": {
                    "minimumSamplesPerGear": 3,
                    "overwriteRatioErrorThreshold": 0.1
                  }
                }
              }
            }
            """);

            var result = ConfigurationLoader.Load(path);

            Assert.True(result.Configuration.Calibration.Live.Enabled);
            Assert.True(result.Configuration.Calibration.Live.AllowOverwrite);
            Assert.Equal(8, result.Configuration.Calibration.Live.PowerCurve.StopSampleCount);
            Assert.Equal(0.5f, result.Configuration.Calibration.Live.PowerCurve.StopRpmCoverageFraction);
            Assert.Equal(3, result.Configuration.Calibration.Live.GearShift.MinimumSamplesPerGear);
            Assert.Equal("shared.json", result.Configuration.Calibration.DataFile);
            Assert.Equal("car-names.json", result.Configuration.Calibration.CarOrdinalNamesFile);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void LiveCalibrationStartsAtDrivingEntryAndPersistsAValidPowerCurve()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.PowerCurve.StopSampleCount = 4;
            configuration.Calibration.Live.PowerCurve.StopRpmCoverageFraction = 0.1f;
            var store = new CalibrationDataStore(path);
            var processor = new VehicleStateProcessor(configuration, store);

            foreach (var (rpm, power) in new[] { (2000f, 100f), (3500f, 200f), (5000f, 300f), (6500f, 200f) })
            {
                processor.Process(Snapshot(rpm, power));
            }

            var saved = Assert.Single(store.FindExact(Identity));
            Assert.NotEmpty(saved.PowerCurve);
            Assert.Contains(saved.PowerCurve, point => point.Power > 0f);
            Assert.True(processor.Process(NonDriving()).Powerband.IsLearned);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ExistingCalibrationIsDisplayedWithoutLiveOverwrite()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = false;
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                Identity,
                [
                    new PowerCurvePoint(2000f, 100f),
                    new PowerCurvePoint(4000f, 300f),
                    new PowerCurvePoint(6000f, 100f),
                ],
                new Dictionary<int, List<float>> { [1] = [0.5f, 0.5f] })));

            var processor = new VehicleStateProcessor(configuration, store);
            var state = processor.Process(Snapshot(4000f, 1f) with { Power = 9999f });

            Assert.False(processor.IsCalibrationRecording);
            Assert.Equal(4000f, state.Powerband.PeakPowerRpm);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ShiftRatiosCollectWhenExistingPowerCurveCannotBeOverwritten()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = false;
            configuration.Calibration.Live.GearShift.MinimumSamplesPerGear = 2;
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                Identity,
                [
                    new PowerCurvePoint(2000f, 100f),
                    new PowerCurvePoint(4000f, 300f),
                    new PowerCurvePoint(6000f, 100f),
                ],
                [])
            {
                RawPowerSamples =
                [
                    new PowerCurveSample(2000f, 100f, 1f, 1, 100f, 0f, TimeSpan.Zero),
                ],
            }));

            var processor = new VehicleStateProcessor(configuration, store);
            processor.Process(Snapshot(6000f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3000f, 100f) with { Gear = 2 });
            processor.Process(Snapshot(6200f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3720f, 100f) with { Gear = 2 });

            var saved = Assert.Single(store.FindExact(Identity));
            Assert.Equal([0.55f], saved.ShiftUpRpmDropRatioByGear[1]);
            Assert.Equal(3, saved.PowerCurve.Count);
            Assert.Single(saved.RawPowerSamples);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ShiftLoggerSnapshotComputesSingleAverageDropRatioPerGear()
    {
        var logger = new ShiftUpRpmDropRatioLogger();
        logger.Observe(Snapshot(6000f, 100f) with { Gear = 1 });
        logger.Observe(Snapshot(3000f, 100f) with { Gear = 2 });
        logger.Observe(Snapshot(6200f, 100f) with { Gear = 1 });
        logger.Observe(Snapshot(3720f, 100f) with { Gear = 2 });

        var snapshot = logger.Snapshot();
        Assert.Equal([0.55f], snapshot[1]);
    }

    [Fact]
    public void ShiftLoggerSnapshotsMeasuredDuration()
    {
        var logger = new ShiftUpRpmDropRatioLogger();
        logger.Observe(Snapshot(6000f, 100f) with
        {
            Gear = 1,
            ReceivedAt = TimeSpan.FromMilliseconds(1000),
        });
        logger.Observe(Snapshot(3000f, 100f) with
        {
            Gear = 2,
            ReceivedAt = TimeSpan.FromMilliseconds(1250),
        });

        Assert.Equal([250f], logger.DurationSnapshot());
    }

    [Fact]
    public void ShiftLoggerBridgesNeutralFramesDuringUpshift()
    {
        var logger = new ShiftUpRpmDropRatioLogger();
        logger.Observe(Snapshot(6000f, 100f) with { Gear = 1 });
        logger.Observe(Snapshot(7100f, 100f) with { Gear = 11 });
        logger.Observe(Snapshot(7000f, 100f) with { Gear = 11 });

        Assert.Null(logger.Observe(Snapshot(6100f, 100f) with { Gear = 2 }));
        Assert.Null(logger.Observe(Snapshot(5980f, 100f) with { Gear = 2 }));
        Assert.Null(logger.Observe(Snapshot(6020f, 100f) with { Gear = 2 }));
        Assert.Null(logger.Observe(Snapshot(5400f, 100f) with { Gear = 2 }));
        Assert.Null(logger.Observe(Snapshot(3000f, 100f) with { Gear = 2 }));
        var shift = logger.Observe(Snapshot(3200f, 100f) with { Gear = 2 });

        Assert.NotNull(shift);
        Assert.Equal(1, shift.Value.OutgoingGear);
        Assert.Equal(0.5f, shift.Value.Ratio);
    }

    [Fact]
    public void ShiftRatiosPersistIndependentlyBeforePowerCurveIsReady()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = true;
            configuration.Calibration.Live.PowerCurve.StopSampleCount = 100;
            configuration.Calibration.Live.GearShift.MinimumSamplesPerGear = 2;
            var store = new CalibrationDataStore(path);
            var processor = new VehicleStateProcessor(configuration, store);

            processor.Process(Snapshot(6000f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3000f, 100f) with { Gear = 2 });
            processor.Process(Snapshot(6200f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3720f, 100f) with { Gear = 2 });

            var saved = Assert.Single(store.FindExact(Identity));
            Assert.Empty(saved.PowerCurve);
            Assert.Equal([0.55f], saved.ShiftUpRpmDropRatioByGear[1]);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ShiftRatiosFlushWhenDrivingEndsBeforeTheBatchThreshold()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = true;
            configuration.Calibration.Live.PowerCurve.StopSampleCount = 100;
            configuration.Calibration.Live.GearShift.MinimumSamplesPerGear = 2;
            var store = new CalibrationDataStore(path);
            var processor = new VehicleStateProcessor(configuration, store);

            processor.Process(Snapshot(6000f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3000f, 100f) with { Gear = 2 });
            processor.Process(NonDriving());

            var saved = Assert.Single(store.FindExact(Identity));
            Assert.Equal([0.5f], saved.ShiftUpRpmDropRatioByGear[1]);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ShiftRatioSamplesDoNotCrossDrivingSessions()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = true;
            configuration.Calibration.Live.PowerCurve.StopSampleCount = 100;
            configuration.Calibration.Live.GearShift.MinimumSamplesPerGear = 2;
            var store = new CalibrationDataStore(path);
            var processor = new VehicleStateProcessor(configuration, store);

            processor.Process(Snapshot(6000f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3000f, 100f) with { Gear = 2 });
            processor.Process(NonDriving());
            processor.Process(Snapshot(6200f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(3100f, 100f) with { Gear = 2 });

            var saved = Assert.Single(store.FindExact(Identity));
            Assert.Equal([0.5f], saved.ShiftUpRpmDropRatioByGear[1]);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.7f, 0.7f)]
    public void LiveShiftRatiosSkipOrOverwriteByRelativeError(float liveRatio, float expectedRatio)
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = true;
            configuration.Calibration.Live.PowerCurve.StopSampleCount = 100;
            configuration.Calibration.Live.GearShift.MinimumSamplesPerGear = 2;
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                Identity,
                [new PowerCurvePoint(2000f, 100f), new PowerCurvePoint(4000f, 300f)],
                new Dictionary<int, List<float>> { [1] = [0.5f, 0.5f] })));

            var processor = new VehicleStateProcessor(configuration, store);
            processor.Process(Snapshot(7000f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(7000f * liveRatio, 100f) with { Gear = 2 });
            processor.Process(Snapshot(7200f, 100f) with { Gear = 1 });
            processor.Process(Snapshot(7200f * liveRatio, 100f) with { Gear = 2 });

            var saved = Assert.Single(store.FindExact(Identity));
            Assert.Equal([expectedRatio], saved.ShiftUpRpmDropRatioByGear[1]);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void IncompletePowerCurveContinuesAfterReturningToDriving()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.PowerCurve.StopSampleCount = 4;
            configuration.Calibration.Live.PowerCurve.StopRpmCoverageFraction = 0.1f;
            var store = new CalibrationDataStore(path);
            var processor = new VehicleStateProcessor(configuration, store);

            processor.Process(Snapshot(2000f, 100f));
            processor.Process(Snapshot(3500f, 200f));
            processor.Process(NonDriving());
            Assert.False(processor.IsCalibrationRecording);
            Assert.Empty(store.Load());

            processor.Process(Snapshot(5000f, 300f));
            processor.Process(Snapshot(6500f, 200f));

            Assert.Single(store.FindExact(Identity));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void DeleteHotkeyRearmsBothLiveCalibrations()
    {
        var path = TemporaryPath();
        try
        {
            var configuration = LiveConfiguration();
            configuration.Calibration.Live.AllowOverwrite = false;
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                Identity,
                [new PowerCurvePoint(2000f, 100f), new PowerCurvePoint(4000f, 300f)],
                new Dictionary<int, List<float>> { [1] = [0.5f] })));

            var processor = new VehicleStateProcessor(configuration, store);
            processor.Process(Snapshot(3000f, 100f));
            Assert.True(processor.TryDeleteCurrentCalibration());

            Assert.Empty(store.Load());
            Assert.True(processor.IsCalibrationRecording);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    private static HudConfiguration LiveConfiguration()
    {
        var configuration = new HudConfiguration();
        configuration.Calibration.Live.Enabled = true;
        configuration.Telemetry.Powerband.BinCount = 32;
        configuration.Telemetry.Powerband.MinimumThrottle = 0.9f;
        configuration.Telemetry.Powerband.MinimumSpeed = 0f;
        configuration.Telemetry.TractionControl.Enabled = false;
        return configuration;
    }

    private static readonly VehicleIdentity Identity = new(2871, 800, 2, 6, 8000f);

    private static TelemetrySnapshot Snapshot(float rpm, float power) => TelemetrySnapshot.Empty with
    {
        IsRaceOn = true,
        EngineIdleRpm = 1000f,
        EngineMaxRpm = Identity.MaxRpm,
        CurrentEngineRpm = rpm,
        Speed = 20f,
        Power = power,
        Throttle = 1f,
        Gear = 1,
        CarOrdinal = Identity.CarOrdinal,
        CarPerformanceIndex = Identity.CarPerformanceIndex,
        DrivetrainType = Identity.DrivetrainType,
        NumCylinders = Identity.NumCylinders,
    };

    private static TelemetrySnapshot NonDriving() => TelemetrySnapshot.Empty;

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"forzahud-live-calibration-{Guid.NewGuid():N}.json");

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
