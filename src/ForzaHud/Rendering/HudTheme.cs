using ForzaHud.Configuration;
using ForzaHud.Vehicle;

namespace ForzaHud.Rendering;

/// <summary>
/// Resolved HUD colours. HUD elements ask the theme for a colour by meaning rather than
/// hard-coding one.
/// </summary>
public sealed class HudTheme
{
    public HudColor Primary { get; init; }

    public HudColor Dim { get; init; }

    public HudColor Accent { get; init; }

    public HudColor Warning { get; init; }

    public HudColor Critical { get; init; }

    public static HudTheme From(ThemeSettings settings) => new()
    {
        Primary = settings.Primary,
        Dim = settings.Dim,
        Accent = settings.Accent,
        Warning = settings.Warning,
        Critical = settings.Critical,
    };

    /// <summary>Colour that communicates a tyre's grip state.</summary>
    public HudColor ForGrip(TireGripState state) => state switch
    {
        TireGripState.ApproachingLimit => Warning,
        TireGripState.LongitudinalSlip => Warning,
        TireGripState.LateralSlip => Warning,
        TireGripState.Wheelspin => Critical,
        TireGripState.WheelLock => Critical,
        _ => Dim,
    };
}
