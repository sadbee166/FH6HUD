using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class CalibrationDiagnosticsTests
{
    [Fact]
    public void ConfigurationValidatesLiveCalibrationThresholds()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, """
            {
              "calibration": {
                "live": {
                  "powerCurve": {
                    "stopSampleCount": 0,
                    "stopRpmCoverageFraction": 2,
                    "overwriteShapeErrorThreshold": -1
                  },
                  "gearShift": {
                    "minimumSamplesPerGear": 0,
                    "overwriteRatioErrorThreshold": 2
                  }
                }
              }
            }
            """);

            var result = ConfigurationLoader.Load(path);
            var live = result.Configuration.Calibration.Live;

            Assert.Equal(120, live.PowerCurve.StopSampleCount);
            Assert.Equal(0.75f, live.PowerCurve.StopRpmCoverageFraction);
            Assert.Equal(0.10f, live.PowerCurve.OverwriteShapeErrorThreshold);
            Assert.Equal(4, live.GearShift.MinimumSamplesPerGear);
            Assert.Equal(0.05f, live.GearShift.OverwriteRatioErrorThreshold);
            Assert.True(result.Diagnostics.Count >= 5);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ConfigurationLoadsPowerCurveDipFraction()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, """
            {
              "telemetry": {
                "powerband": {
                  "powerCurveDipFraction": 0.12
                }
              }
            }
            """);

            var result = ConfigurationLoader.Load(path);

            Assert.True(result.IsClean);
            Assert.Equal(0.12f, result.Configuration.Telemetry.Powerband.PowerCurveDipFraction);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ConfigurationLoadsRawPowerCurveLearningSettings()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, """
            {
              "telemetry": {
                "powerband": {
                  "maximumRawSamples": 900,
                  "maximumSamplesPerBin": 12,
                  "minimumSamplesPerBin": 4,
                  "minimumIndependentPassesPerBin": 3,
                  "upperPowerQuantile": 0.8,
                  "smoothingWindowRpm": 275,
                  "shapeCleaningEnabled": false,
                  "shapeCleaningIterations": 2,
                  "sampleResidualThreshold": 0.05,
                  "maximumNarrowValleyRpmSpan": 375,
                  "minimumValleyRecoveryFraction": 0.96,
                  "maximumRpmRate": 18000,
                  "rpmRateChangeFraction": 0.4
                }
              }
            }
            """);

            var result = ConfigurationLoader.Load(path);
            var settings = result.Configuration.Telemetry.Powerband;

            Assert.True(result.IsClean);
            Assert.Equal(900, settings.MaximumRawSamples);
            Assert.Equal(12, settings.MaximumSamplesPerBin);
            Assert.Equal(4, settings.MinimumSamplesPerBin);
            Assert.Equal(3, settings.MinimumIndependentPassesPerBin);
            Assert.Equal(0.8f, settings.UpperPowerQuantile);
            Assert.Equal(275f, settings.SmoothingWindowRpm);
            Assert.False(settings.ShapeCleaningEnabled);
            Assert.Equal(2, settings.ShapeCleaningIterations);
            Assert.Equal(0.96f, settings.MinimumValleyRecoveryFraction);
            Assert.Equal(18000f, settings.MaximumRpmRate);
            Assert.Equal(0.4f, settings.RpmRateChangeFraction);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ManualCalibrationStillReportsFailureWithoutPowerSamples()
    {
        var output = new List<string>();
        var configuration = new HudConfiguration();
        var processor = new VehicleStateProcessor(configuration, calibrationOutput: output.Add);

        processor.ToggleCalibrationRecording();
        var result = processor.ToggleCalibrationRecording();

        Assert.False(result.Saved);
        Assert.Contains(output, line => line.Contains("FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public void SavedCalibrationContainsRawCurvesAndOneRatioPerGear()
    {
        var path = TemporaryPath();
        try
        {
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                new VehicleIdentity(10, 800, 2, 6, 8000f),
                [new PowerCurvePoint(3000f, 100f)],
                new Dictionary<int, List<float>> { [1] = [0.5f, 0.6f] })));

            var saved = File.ReadAllText(path);
            Assert.Contains("PowerCurve", saved);
            Assert.Contains("ShiftUpRpmDropRatioByGear", saved);
            Assert.Contains("0.55", saved);
            Assert.DoesNotContain("0.6", saved);
            Assert.DoesNotContain("Measurement", saved);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    private static TelemetrySnapshot Snapshot(float rpm) => TelemetrySnapshot.Empty with
    {
        IsRaceOn = true,
        EngineIdleRpm = 1000f,
        EngineMaxRpm = 8000f,
        CurrentEngineRpm = rpm,
        Speed = 20f,
        Power = 100f,
        Throttle = 1f,
        Gear = 1,
        CarOrdinal = 10,
        CarPerformanceIndex = 800,
        DrivetrainType = 2,
        NumCylinders = 6,
    };

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"forzahud-diagnostics-{Guid.NewGuid():N}.json");

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
