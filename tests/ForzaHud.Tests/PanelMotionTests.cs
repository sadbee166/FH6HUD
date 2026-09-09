using ForzaHud.Configuration;
using ForzaHud.Hud;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class PanelMotionTests
{
    [Fact]
    public void GForceCalculatorCalculatesVerticalG()
    {
        var settings = new GForceSettings { InvertVertical = false };
        Assert.Equal(1f, GForceCalculator.Vertical(9.80665f, settings), precision: 4);

        settings.InvertVertical = true;
        Assert.Equal(-1f, GForceCalculator.Vertical(9.80665f, settings), precision: 4);
    }

    [Fact]
    public void VehicleStateProcessorCalculatesVerticalG()
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());
        var snapshot = TelemetrySnapshot.Empty with
        {
            AccelerationY = 9.80665f * 1.5f,
        };

        var state = processor.Process(snapshot);
        Assert.Equal(1.5f, state.VerticalG, precision: 4);
    }

    [Fact]
    public void HudEngineSmoothsAndResetsVerticalG()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.Smoothing.GForceMilliseconds = 50;
        configuration.Visual.PanelMotion.SmoothingMilliseconds = 100;
        var engine = new HudEngine(configuration);

        engine.Update(new DerivedState { VerticalG = 0f }, deltaSeconds: 0.016);
        Assert.Equal(0f, engine.Display.VerticalG);
        Assert.Equal(0f, engine.Display.PanelMotionVerticalG);

        var state1 = new DerivedState { VerticalG = 10f };
        engine.Update(state1, deltaSeconds: 0.05);
        Assert.True(engine.Display.VerticalG > 0f && engine.Display.VerticalG < 10f);
        Assert.True(engine.Display.PanelMotionVerticalG > 0f && engine.Display.PanelMotionVerticalG < 10f);

        var resetState = new DerivedState { VerticalG = -5f };
        engine.Reset(resetState);
        engine.Update(resetState, deltaSeconds: 0.05);
        Assert.Equal(-5f, engine.Display.VerticalG);
        Assert.Equal(-5f, engine.Display.PanelMotionVerticalG);
    }

    [Fact]
    public void PanelMotionCalculatorAppliesVerticalAndLongitudinalGToVerticalOffset()
    {
        var settings = new PanelMotionSettings
        {
            Enabled = true,
            VerticalMovePerLongitudinalG = 0.025f,
            VerticalMovePerVerticalG = 0.050f,
            HorizontalMovePerLateralG = 0.020f,
            SpeedShake = new SpeedShakeSettings { Enabled = false },
        };
        var display = new HudDisplay
        {
            PanelMotionLateralG = 1.0f,
            PanelMotionLongitudinalG = 2.0f,
            PanelMotionVerticalG = 3.0f,
        };

        var motion = PanelMotionCalculator.Calculate(display, settings, reference: 1000f);

        // X = 1.0 * 0.020 * 1000 = 20
        // Y = (2.0 * 0.025 + 3.0 * 0.050) * 1000 = (0.05 + 0.15) * 1000 = 200
        Assert.Equal(20f, motion.Offset.X, precision: 4);
        Assert.Equal(200f, motion.Offset.Y, precision: 4);
    }

    [Fact]
    public void PanelMotionCalculatorScalesDownWithHigherSpeed()
    {
        var settings = new PanelMotionSettings
        {
            Enabled = true,
            ScalePerLongitudinalG = 0.08f,
            ScalePer100Kph = 0.05f,
            MinimumScale = 0.5f,
            MaximumScale = 1.5f,
            SpeedShake = new SpeedShakeSettings { Enabled = false },
        };
        var display = new HudDisplay
        {
            SpeedKph = 200f,
            PanelMotionLongitudinalG = 0f,
        };

        var motion = PanelMotionCalculator.Calculate(display, settings, reference: 1000f);

        // scale = 1.0 - 0 - (200 / 100 * 0.05) = 0.90
        Assert.Equal(0.90f, motion.Scale, precision: 4);
    }

    [Fact]
    public void PanelMotionCalculatorCombinesLongitudinalGAndSpeedForScale()
    {
        var settings = new PanelMotionSettings
        {
            Enabled = true,
            ScalePerLongitudinalG = 0.10f,
            ScalePer100Kph = 0.05f,
            MinimumScale = 0.5f,
            MaximumScale = 1.5f,
            SpeedShake = new SpeedShakeSettings { Enabled = false },
        };
        var display = new HudDisplay
        {
            SpeedKph = 100f,
            PanelMotionLongitudinalG = 1.0f,
        };

        var motion = PanelMotionCalculator.Calculate(display, settings, reference: 1000f);

        // scale = 1.0 - 1.0 * 0.10 - (100 / 100 * 0.05) = 0.85
        Assert.Equal(0.85f, motion.Scale, precision: 4);
    }

    [Fact]
    public void ConfigurationLoaderAllowsZeroMinimumScaleWithoutFallback()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            const string json = """
            {
              "visual": {
                "panelMotion": {
                  "minimumScale": 0.0
                }
              }
            }
            """;
            File.WriteAllText(tempFile, json);

            var result = ConfigurationLoader.Load(tempFile);
            Assert.Equal(0f, result.Configuration.Visual.PanelMotion.MinimumScale);
            Assert.DoesNotContain(result.Diagnostics, d => d.Contains("minimumScale"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
