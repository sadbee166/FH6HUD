using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class ShiftOptimizationCalibrationTests
{
    [Fact]
    public void RecorderRetainsSpeedAndDerivedAccelerationForShiftOptimization()
    {
        var settings = new PowerbandSettings
        {
            MinimumSpeed = 0f,
            MinimumSamplesPerBin = 1,
            MinimumIndependentPassesPerBin = 1,
            ShapeCleaningIterations = 1,
            SampleResidualThreshold = 1f,
            PowerCurveDipFraction = 1f,
        };
        var recorder = new CalibrationRecorder(settings);
        recorder.Start(Snapshot(2000f, 20f, 1));
        recorder.Record(Snapshot(2200f, 21f, 101));

        Assert.Equal(21f, recorder.RawPowerSamples[^1].SpeedMetersPerSecond);
        Assert.Equal(10f, recorder.RawPowerSamples[^1].LongitudinalAcceleration, precision: 3);
    }

    [Fact]
    public void ConfigurationValidatesShiftOptimizationSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-shift-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "telemetry": {
                "powerband": {
                  "shiftOptimizationCandidateStepRpm": 0,
                  "minimumShiftOptimizationSamplesPerGear": 1,
                  "shiftOptimizationSpeedIntegrationSteps": 0
                }
              }
            }
            """);

            var result = ConfigurationLoader.Load(path);

            Assert.Equal(25f, result.Configuration.Telemetry.Powerband.ShiftOptimizationCandidateStepRpm);
            Assert.Equal(8, result.Configuration.Telemetry.Powerband.MinimumShiftOptimizationSamplesPerGear);
            Assert.Equal(48, result.Configuration.Telemetry.Powerband.ShiftOptimizationSpeedIntegrationSteps);
            Assert.True(result.Diagnostics.Count >= 3);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void CalibrationPersistsShiftPerformanceSamplesAndDurations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-shift-performance-{Guid.NewGuid():N}.json");
        try
        {
            var identity = new VehicleIdentity(2871, 800, 2, 6, 8000f);
            var sample = new PowerCurveSample(4000f, 300f, 1f, 1, 1000f, 0f, TimeSpan.FromSeconds(1))
            {
                SpeedMetersPerSecond = 40f,
                LongitudinalAcceleration = 8f,
            };
            var calibration = new VehicleCalibration(
                identity,
                [new PowerCurvePoint(4000f, 300f)],
                new Dictionary<int, List<float>> { [1] = [0.8f] })
            {
                RawPowerSamples = [sample],
                ShiftDurationMilliseconds = [250f],
            };

            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(calibration));

            var saved = Assert.Single(store.FindExact(identity));
            Assert.Equal(sample, Assert.Single(saved.RawPowerSamples));
            Assert.Equal([250f], saved.ShiftDurationMilliseconds);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static TelemetrySnapshot Snapshot(float rpm, float speed, int milliseconds) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 8000f,
            CurrentEngineRpm = rpm,
            Speed = speed,
            Power = 100f,
            Throttle = 1f,
            Gear = 1,
            CarOrdinal = 2871,
            CarPerformanceIndex = 800,
            DrivetrainType = 2,
            NumCylinders = 6,
            ReceivedAt = TimeSpan.FromMilliseconds(milliseconds),
        };
}
