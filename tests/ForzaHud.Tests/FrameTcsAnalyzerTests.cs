using ForzaHud.Configuration;
using ForzaHud.Platform.Windows;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class FrameTcsAnalyzerTests
{
    [Fact]
    public void CyanIndicatorPixelsActivateTheDetector()
    {
        var analyzer = new FrameTcsAnalyzer(new FrameTcsSettings
        {
            MinimumOnPixels = 2,
            MinimumCyanChannel = 120,
            MinimumCyanDominance = 50,
        });

        var pixels = new byte[]
        {
            245, 226, 37, 0,
            218, 199, 46, 0,
            80, 100, 100, 0,
        };

        Assert.True(analyzer.Update(pixels));
        Assert.True(analyzer.IsActive);
        Assert.Equal(2, analyzer.LastOnPixelCount);
    }

    [Fact]
    public void GrayIndicatorPixelsRemainOff()
    {
        var analyzer = new FrameTcsAnalyzer(new FrameTcsSettings
        {
            MinimumOnPixels = 1,
            MinimumCyanChannel = 120,
            MinimumCyanDominance = 50,
        });

        var pixels = new byte[]
        {
            170, 170, 170, 0,
            90, 95, 100, 0,
        };

        Assert.False(analyzer.Update(pixels));
        Assert.False(analyzer.IsActive);
        Assert.Equal(0, analyzer.LastOnPixelCount);
    }

    [Fact]
    public void DefaultCaptureRegionExcludesTheUpperAbsIndicatorBand()
    {
        var region = ScreenRegionCapture.CalculateRegion(
            new MonitorHelper.MonitorBounds(0, 0, 2560, 1440, true),
            new FrameTcsSettings());

        Assert.True(region.Y >= 1330);
        Assert.True(region.Y + region.Height <= 1365);
    }

    [Fact]
    public void FrameModeSkipsTelemetryTcsDetection()
    {
        var configuration = new HudConfiguration();
        configuration.Telemetry.TractionControl.DetectionMode = TcsDetectionMode.Frame;
        configuration.Telemetry.TractionControl.ActivationSamples = 1;
        var processor = new VehicleStateProcessor(configuration);

        var first = Snapshot(0.000, 300f);
        var cut = Snapshot(0.016, 100f);

        Assert.False(processor.Process(first).TractionControlActive);
        Assert.False(processor.Process(cut).TractionControlActive);
    }

    [Fact]
    public void FrameModeStillExcludesTelemetryTcsSamplesFromPowerCalibration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-frame-tcs-{Guid.NewGuid():N}.json");
        var configuration = new HudConfiguration();
        configuration.Calibration.VerboseOutput = false;
        configuration.Calibration.Live.Enabled = false;
        configuration.Telemetry.TractionControl.DetectionMode = TcsDetectionMode.Frame;
        configuration.Telemetry.TractionControl.ActivationSamples = 1;
        configuration.Telemetry.TractionControl.DeactivationSamples = 1;
        configuration.Telemetry.TractionControl.AttackMilliseconds = 0;
        configuration.Telemetry.TractionControl.ReleaseMilliseconds = 0;
        configuration.Telemetry.TractionControl.MinimumDrivenSlip = 0.10f;
        configuration.Telemetry.TractionControl.MinimumDrivenSlipIncrease = 0.02f;
        var store = new CalibrationDataStore(path);
        var processor = new VehicleStateProcessor(configuration, store);
        var initial = Snapshot(2000f, 100f, 0f, 0);

        try
        {
            processor.Process(initial);
            Assert.True(processor.ToggleCalibrationRecording().IsRecording);

            processor.Process(Snapshot(2100f, 100f, 0.20f, 16), frameTcsActive: true);
            processor.Process(Snapshot(2200f, 100f, 0.20f, 32), frameTcsActive: true);
            processor.Process(Snapshot(2300f, 300f, 0f, 48), frameTcsActive: false);

            Assert.True(processor.ToggleCalibrationRecording().Saved);
            var identity = new VehicleIdentity(1, 0, 2, 4, 9000f);
            var saved = Assert.Single(store.FindExact(identity));
            Assert.Equal([2000f, 2300f], saved.RawPowerSamples.Select(sample => sample.Rpm));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(store.RawSamplesDirectory))
            {
                Directory.Delete(store.RawSamplesDirectory, recursive: true);
            }
        }
    }

    private static TelemetrySnapshot Snapshot(double seconds, float power) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            ReceivedAt = TimeSpan.FromSeconds(seconds),
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 8000f,
            CurrentEngineRpm = 4000f,
            Speed = 20f,
            Power = power,
            Throttle = 1f,
            Gear = 3,
            CarOrdinal = 1,
            NumCylinders = 4,
        };

    private static TelemetrySnapshot Snapshot(
        float rpm,
        float power,
        float slip,
        int milliseconds) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            ReceivedAt = TimeSpan.FromMilliseconds(milliseconds),
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 9000f,
            CurrentEngineRpm = rpm,
            Speed = 20f,
            Power = power,
            Throttle = 1f,
            Gear = 2,
            CarOrdinal = 1,
            DrivetrainType = 2,
            NumCylinders = 4,
            TireSlipRatioRearLeft = slip,
            TireSlipRatioRearRight = slip,
        };
}
