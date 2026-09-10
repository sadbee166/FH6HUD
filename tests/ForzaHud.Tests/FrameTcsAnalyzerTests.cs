using ForzaHud.Configuration;
using ForzaHud.Platform.Windows;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class FrameTcsAnalyzerTests
{
    [Fact]
    public void CyanTcrShapeActivatesTheDetector()
    {
        var analyzer = new FrameTcsAnalyzer(new FrameTcsSettings
        {
            Template = "##./#../###",
            TemplateWidthFraction = 0.20f,
            TemplateHeightFraction = 0.30f,
            MinimumOnPixels = 2,
            MinimumCyanChannel = 120,
            MinimumCyanDominance = 50,
        });

        var pixels = new byte[20 * 10 * 4];
        PaintCyan(pixels, 20, 12, 5, "##./#../###");

        Assert.True(analyzer.Update(pixels, 20, 10));
        Assert.True(analyzer.IsActive);
        Assert.Equal(6, analyzer.LastOnPixelCount);
        Assert.True(analyzer.LastShapeMatch >= 0.78f);
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

        Assert.False(analyzer.Update(pixels, 2, 1));
        Assert.False(analyzer.IsActive);
        Assert.Equal(0, analyzer.LastOnPixelCount);
    }

    [Fact]
    public void DefaultCaptureRegionCoversTheSpeedometerSearchArea()
    {
        var region = ScreenRegionCapture.CalculateRegion(
            new MonitorHelper.MonitorBounds(0, 0, 2560, 1440, true),
            new FrameTcsSettings());

        Assert.Equal(2022, region.X);
        Assert.Equal(1181, region.Y);
        Assert.Equal(512, region.Width);
        Assert.Equal(202, region.Height);
    }

    [Fact]
    public void CyanPixelsWithoutTheTcrShapeRemainOff()
    {
        var analyzer = new FrameTcsAnalyzer(new FrameTcsSettings
        {
            Template = "##./#../###",
            TemplateWidthFraction = 0.20f,
            TemplateHeightFraction = 0.30f,
            MinimumOnPixels = 2,
            MinimumForegroundMatch = 0.90f,
            MinimumShapeMatch = 0.90f,
        });

        var pixels = new byte[20 * 10 * 4];
        PaintCyan(pixels, 20, 12, 5, "###/###/###");

        Assert.False(analyzer.Update(pixels, 20, 10));
        Assert.False(analyzer.IsActive);
    }

    [Fact]
    public void CyanAbsShapeDoesNotActivateTheDetector()
    {
        var settings = new FrameTcsSettings();
        var analyzer = new FrameTcsAnalyzer(settings);
        var pixels = new byte[512 * 202 * 4];
        var a = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" };
        var b = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." };
        var s = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." };
        var abs = a.Select((row, index) => row + "." + b[index] + "." + s[index]).ToArray();

        PaintScaledCyan(pixels, 512, 300, 120, settings, abs);

        Assert.False(analyzer.Update(pixels, 512, 202));
        Assert.True(analyzer.LastOnPixelCount > 0);
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

    private static void PaintCyan(byte[] pixels, int width, int x, int y, string template)
    {
        var rows = template.Split('/');
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                if (rows[row][column] != '#')
                {
                    continue;
                }

                var offset = ((y + row) * width + x + column) * 4;
                pixels[offset] = 220;
                pixels[offset + 1] = 220;
                pixels[offset + 2] = 20;
            }
        }
    }

    private static void PaintScaledCyan(
        byte[] pixels,
        int width,
        int x,
        int y,
        FrameTcsSettings settings,
        IReadOnlyList<string> template)
    {
        var templateWidth = template[0].Length;
        var templateHeight = template.Count;
        var shapeWidth = Math.Max(templateWidth, (int)MathF.Round(width * settings.TemplateWidthFraction));
        var shapeHeight = Math.Max(templateHeight, (int)MathF.Round(202 * settings.TemplateHeightFraction));

        for (var row = 0; row < templateHeight; row++)
        {
            var top = y + row * shapeHeight / templateHeight;
            var bottom = y + (row + 1) * shapeHeight / templateHeight;
            for (var column = 0; column < templateWidth; column++)
            {
                if (template[row][column] != '#')
                {
                    continue;
                }

                var left = x + column * shapeWidth / templateWidth;
                var right = x + (column + 1) * shapeWidth / templateWidth;
                for (var pixelY = top; pixelY < bottom; pixelY++)
                {
                    for (var pixelX = left; pixelX < right; pixelX++)
                    {
                        var offset = (pixelY * width + pixelX) * 4;
                        pixels[offset] = 220;
                        pixels[offset + 1] = 220;
                        pixels[offset + 2] = 20;
                    }
                }
            }
        }
    }
}
