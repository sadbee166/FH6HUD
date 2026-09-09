using ForzaHud.Configuration;
using Xunit;

namespace ForzaHud.Tests;

public sealed class VisualOpacityTests
{
    [Fact]
    public void ConfigurationLoadsVisualOpacityBlock()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"forzahud-opacity-test-{Guid.NewGuid():N}.json");
        try
        {
            var json = """
            {
              "visual": {
                "opacity": {
                  "arcTrack": 1.0,
                  "gForceTrace": 0.25
                }
              }
            }
            """;
            File.WriteAllText(tempFile, json);

            var result = ConfigurationLoader.Load(tempFile);

            Assert.True(result.IsClean);
            Assert.Equal(1f, result.Configuration.Visual.Opacity.ArcTrack);
            Assert.Equal(0.25f, result.Configuration.Visual.Opacity.GForceTrace);
            Assert.Equal(0.70f, result.Configuration.Visual.Opacity.ArcMarker);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
