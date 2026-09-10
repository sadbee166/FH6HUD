using ForzaHud.Configuration;
using Xunit;

namespace ForzaHud.Tests;

public sealed class ConfigurationReloadTests
{
    [Fact]
    public void FileChangesBecomePendingAfterTheSaveDebounce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-watch-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            using var watcher = new ConfigurationFileWatcher(path);
            File.WriteAllText(path, "{\"overlay\":{\"opacity\":0.5}}");

            Assert.True(SpinWait.SpinUntil(watcher.TryConsumeChange, TimeSpan.FromSeconds(3)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidJsonIsNotEligibleForLiveApplication()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-invalid-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ invalid");

            var result = ConfigurationLoader.Load(path);

            Assert.False(result.CanApply);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyFromUpdatesValuesWithoutReplacingCapturedSettings()
    {
        var current = new HudConfiguration();
        var visual = current.Visual;
        var powerband = current.Telemetry.Powerband;
        var tractionControl = current.Telemetry.TractionControl;
        var frame = tractionControl.Frame;
        var elements = new Dictionary<string, ElementSettings>
        {
            [DefaultElements.Ids.Speed] = new ElementSettings(X: 0.7),
        };
        var incoming = new HudConfiguration
        {
            Overlay = new OverlaySettings { Opacity = 0.5 },
            Visual = new VisualSettings
            {
                ReticleRadius = 0.42f,
                ReticleThicknessMultiplier = 1.75f,
                LineCornerRadius = 0.35f,
            },
            Telemetry = new TelemetrySettings
            {
                Powerband = new PowerbandSettings { BinCount = 128 },
                TractionControl = new TractionControlSettings
                {
                    Frame = new FrameTcsSettings { RegionX = 0.8f },
                },
            },
            Elements = elements,
        };

        current.ApplyFrom(incoming);

        Assert.Same(visual, current.Visual);
        Assert.Same(powerband, current.Telemetry.Powerband);
        Assert.Same(tractionControl, current.Telemetry.TractionControl);
        Assert.Same(frame, current.Telemetry.TractionControl.Frame);
        Assert.Equal(0.5, current.Overlay.Opacity);
        Assert.Equal(0.42f, current.Visual.ReticleRadius);
        Assert.Equal(1.75f, current.Visual.ReticleThicknessMultiplier);
        Assert.Equal(0.35f, current.Visual.LineCornerRadius);
        Assert.Equal(128, current.Telemetry.Powerband.BinCount);
        Assert.Equal(0.8f, current.Telemetry.TractionControl.Frame.RegionX);
        Assert.Same(elements, current.Elements);
    }
}
