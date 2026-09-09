using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud;

/// <summary>Calculates deterministic two-axis shake from speed and animation time.</summary>
public static class SpeedShakeCalculator
{
    /// <summary>
    /// Returns a shake offset whose strength ramps from zero at the configured start speed
    /// to full amplitude at the configured full-strength speed.
    /// </summary>
    public static HudPoint Calculate(
        float speedKph,
        double animationTimeSeconds,
        SpeedShakeSettings settings,
        float reference)
    {
        if (!settings.Enabled || settings.Amplitude <= 0f || settings.FrequencyHz <= 0f)
        {
            return new HudPoint(0f, 0f);
        }

        var strength = Math.Clamp(
            (speedKph - settings.StartSpeedKph)
            / (settings.FullStrengthSpeedKph - settings.StartSpeedKph),
            0f,
            1f);
        if (strength <= 0f)
        {
            return new HudPoint(0f, 0f);
        }

        var phase = animationTimeSeconds * Math.PI * 2d * settings.FrequencyHz;
        var x = Math.Sin(phase) * 0.7d
            + Math.Sin(phase * 1.71d + 1.3d) * 0.3d;
        var y = Math.Cos(phase * 1.29d + 0.8d) * 0.7d
            + Math.Sin(phase * 1.93d + 2.1d) * 0.3d;
        var distance = settings.Amplitude * reference * strength;

        return new HudPoint((float)(x * distance), (float)(y * distance));
    }
}
