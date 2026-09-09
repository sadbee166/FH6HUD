using System.Text.Json;
using ForzaHud.Configuration;
using ForzaHud.Rendering;
using Xunit;

namespace ForzaHud.Tests;

public sealed class HudColorTests
{
    [Fact]
    public void ConstructorSetsRgbaValuesWithDefaultAlpha()
    {
        var colorDefaultAlpha = new HudColor(10, 20, 30);
        Assert.Equal(10, colorDefaultAlpha.R);
        Assert.Equal(20, colorDefaultAlpha.G);
        Assert.Equal(30, colorDefaultAlpha.B);
        Assert.Equal(255, colorDefaultAlpha.A);

        var colorExplicitAlpha = new HudColor(10, 20, 30, 128);
        Assert.Equal(128, colorExplicitAlpha.A);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("""{"r": 255, "g": 128, "b": 64, "a": 200}""", 255, 128, 64, 200)]
    [InlineData("""{"r": 95, "g": 208, "b": 255}""", 95, 208, 255, 255)]
    [InlineData("""{"R": 10, "G": 20, "B": 30, "A": 40}""", 10, 20, 30, 40)]
    public void JsonDeserializesRgbaObject(string json, byte r, byte g, byte b, byte a)
    {
        var color = JsonSerializer.Deserialize<HudColor>(json, JsonOptions);

        Assert.Equal(new HudColor(r, g, b, a), color);
    }

    [Fact]
    public void HudThemeFromResolvesAllThemeColorsWithAlpha()
    {
        var settings = new ThemeSettings
        {
            Primary = new HudColor(17, 34, 51, 128),
            Dim = new HudColor(68, 85, 102, 160),
            Accent = new HudColor(119, 136, 153, 192),
            Warning = new HudColor(170, 187, 204, 208),
            Critical = new HudColor(221, 238, 255, 224),
        };

        var theme = HudTheme.From(settings);

        Assert.Equal(new HudColor(17, 34, 51, 128), theme.Primary);
        Assert.Equal(new HudColor(68, 85, 102, 160), theme.Dim);
        Assert.Equal(new HudColor(119, 136, 153, 192), theme.Accent);
        Assert.Equal(new HudColor(170, 187, 204, 208), theme.Warning);
        Assert.Equal(new HudColor(221, 238, 255, 224), theme.Critical);
    }

    [Theory]
    [InlineData(255, 1.0f, 255)]
    [InlineData(255, 0.5f, 128)]
    [InlineData(128, 0.5f, 64)]
    [InlineData(200, 0.8f, 160)]
    [InlineData(0, 1.0f, 0)]
    [InlineData(255, 0.0f, 0)]
    public void EffectiveAlphaOverlapsColorAlphaAndOpacity(byte colorAlpha, float elementOpacity, byte expectedEffectiveAlpha)
    {
        var color = new HudColor(100, 150, 200, colorAlpha);
        var paint = new HudPaint(color, elementOpacity);

        Assert.Equal(expectedEffectiveAlpha, paint.EffectiveAlpha);
    }

    [Fact]
    public void ConfigurationLoaderLoadsRgbaThemeFromJson()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"forzahud-theme-test-{Guid.NewGuid():N}.json");
        try
        {
            var json = """
            {
              "visual": {
                "theme": {
                  "primary": { "r": 250, "g": 251, "b": 252, "a": 200 },
                  "accent": { "r": 90, "g": 200, "b": 250, "a": 180 }
                }
              }
            }
            """;
            File.WriteAllText(tempFile, json);

            var result = ConfigurationLoader.Load(tempFile);

            Assert.True(result.IsClean);
            Assert.Equal(new HudColor(250, 251, 252, 200), result.Configuration.Visual.Theme.Primary);
            Assert.Equal(new HudColor(90, 200, 250, 180), result.Configuration.Visual.Theme.Accent);
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
