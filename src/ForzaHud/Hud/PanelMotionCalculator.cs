using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud;

/// <summary>Complete-panel transform produced from the smoothed display values.</summary>
public readonly record struct PanelMotion(
    float Scale,
    HudPoint Offset,
    float RotationDegrees = 0f,
    float DepthYawDegrees = 0f,
    float DepthPitchDegrees = 0f)
{
    /// <summary>Panel transform with no G-force, roll, or depth response.</summary>
    public static PanelMotion Identity => new(1f, new HudPoint(0f, 0f), 0f);

    /// <summary>Scales and rotates around <paramref name="anchor"/> and then applies the panel offset.</summary>
    public HudPoint Apply(HudPoint point, HudPoint anchor)
    {
        var dx = (point.X - anchor.X) * Scale;
        var dy = (point.Y - anchor.Y) * Scale;

        if (RotationDegrees != 0f)
        {
            var radians = MathF.PI * RotationDegrees / 180f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);
            var rx = dx * cos - dy * sin;
            var ry = dx * sin + dy * cos;
            dx = rx;
            dy = ry;
        }

        return new HudPoint(anchor.X + dx + Offset.X, anchor.Y + dy + Offset.Y);
    }
}

/// <summary>Maps smoothed G-force, roll, and angular-velocity values to the panel transform.</summary>
public static class PanelMotionCalculator
{
    /// <summary>
    /// Calculates the current panel transform. Positive longitudinal G shrinks and moves
    /// down; negative longitudinal G enlarges and moves up. Lateral G moves in its signed
    /// force direction. Roll angle rotates around the panel anchor. Angular velocity
    /// produces bounded yaw and pitch depth angles.
    /// </summary>
    public static PanelMotion Calculate(
        HudDisplay display,
        PanelMotionSettings settings,
        float reference)
    {
        if (!settings.Enabled)
        {
            return PanelMotion.Identity;
        }

        var longitudinalG = display.PanelMotionLongitudinalG;
        var scale = Math.Clamp(
            1f - longitudinalG * settings.ScalePerLongitudinalG - (display.SpeedKph / 100f) * settings.ScalePer100Kph,
            settings.MinimumScale,
            settings.MaximumScale);

        var gForceOffset = new HudPoint(
            display.PanelMotionLateralG * settings.HorizontalMovePerLateralG * reference,
            (longitudinalG * settings.VerticalMovePerLongitudinalG + display.PanelMotionVerticalG * settings.VerticalMovePerVerticalG) * reference);
        var speedShakeOffset = SpeedShakeCalculator.Calculate(
            display.SpeedKph,
            display.AnimationTimeSeconds,
            settings.SpeedShake,
            reference);

        var rotation = display.PanelMotionRollDegrees * settings.RollMultiplier;
        var depthYaw = Math.Clamp(
            display.PanelMotionYawAngularVelocity * settings.YawDegreesPerAngularVelocity,
            -settings.MaximumYawRotationDegrees,
            settings.MaximumYawRotationDegrees);
        var depthPitch = Math.Clamp(
            display.PanelMotionPitchAngularVelocity * settings.PitchDegreesPerAngularVelocity,
            -settings.MaximumPitchRotationDegrees,
            settings.MaximumPitchRotationDegrees);

        return new PanelMotion(scale, gForceOffset + speedShakeOffset, rotation, depthYaw, depthPitch);
    }
}
