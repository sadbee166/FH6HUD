using ForzaHud.Configuration;
using ForzaHud.Rendering;
using ForzaHud.Vehicle;

namespace ForzaHud.Hud;

/// <summary>
/// Values the HUD actually draws: the derived state plus the smoothed quantities used for
/// graphical movement.
///
/// Driver inputs are kept unsmoothed by configuration, so the HUD responds immediately.
/// </summary>
public sealed class HudDisplay
{
    public float Rpm { get; set; }

    /// <summary>RPM normalized between idle and maximum.</summary>
    public float RpmNormalized { get; set; }

    public float Speed { get; set; }

    /// <summary>Speed in KPH after the display speed smoothing.</summary>
    public float SpeedKph { get; set; }

    /// <summary>Elapsed render time used by deterministic animated HUD effects.</summary>
    public double AnimationTimeSeconds { get; set; }

    public float Throttle { get; set; }

    public float Brake { get; set; }

    public float Steer { get; set; }

    public float Boost { get; set; }

    /// <summary>Highest boost seen this session, used to scale the boost arc.</summary>
    public float BoostMaximum { get; set; } = 10f;

    public float LateralG { get; set; }

    public float LongitudinalG { get; set; }

    public float VerticalG { get; set; }

    /// <summary>Lateral G after the panel-motion-specific smoothing.</summary>
    public float PanelMotionLateralG { get; set; }

    /// <summary>Longitudinal G after the panel-motion-specific smoothing.</summary>
    public float PanelMotionLongitudinalG { get; set; }

    /// <summary>Vertical G after the panel-motion-specific smoothing.</summary>
    public float PanelMotionVerticalG { get; set; }

    /// <summary>Roll angle in degrees after the panel-motion-specific smoothing.</summary>
    public float PanelMotionRollDegrees { get; set; }

    /// <summary>Pitch angular acceleration after the panel-motion-specific smoothing.</summary>
    public float PanelMotionPitchAngularAcceleration { get; set; }

    /// <summary>Yaw angular acceleration after the panel-motion-specific smoothing.</summary>
    public float PanelMotionYawAngularAcceleration { get; set; }
}

/// <summary>
/// Everything an element needs for one frame. Elements receive this rather than reading
/// configuration or telemetry directly.
/// </summary>
/// <param name="State">Derived vehicle state.</param>
/// <param name="Display">Smoothed display values.</param>
/// <param name="Visual">Visual settings.</param>
/// <param name="Theme">Resolved colours.</param>
/// <param name="Origin">Element centre in device-independent pixels.</param>
/// <param name="Scale">Element scale multiplier.</param>
/// <param name="Opacity">Combined overlay and element opacity.</param>
/// <param name="Width">Overlay width in device-independent pixels.</param>
    /// <param name="GForceFullScale">Acceleration in G that maps to the edge of the G indicator.</param>
    /// <param name="Height">Overlay height in device-independent pixels.</param>
public readonly record struct HudFrame(
    DerivedState State,
    HudDisplay Display,
    VisualSettings Visual,
    HudTheme Theme,
    HudPoint Origin,
    float Scale,
    float Opacity,
    float Width,
    float Height,
    float GForceFullScale)
{
    /// <summary>Shorter overlay dimension, the reference most HUD sizing is relative to.</summary>
    public float Reference => Math.Min(Width, Height);
}

/// <summary>
/// One drawable part of the HUD.
///
/// Elements decide what to draw and where, and never perform vehicle calculations or read
/// raw telemetry.
/// </summary>
public interface IHudElement
{
    /// <summary>Element id, matching the key used in the configuration file.</summary>
    string Id { get; }

    void Draw(IRenderContext context, in HudFrame frame);
}
