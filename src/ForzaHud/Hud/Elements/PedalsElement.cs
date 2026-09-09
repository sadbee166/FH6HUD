using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Accelerator and brake as nested right-side arc meters.
///
/// These are driver inputs, so they are drawn with no meaningful smoothing: the arcs have to
/// feel connected to the pedal. Raw telemetry remains available on the derived state even
/// though this representation is a plain fill fraction. Brake is the middle ring and throttle
/// is the outer ring, matching the visual sequence in AGENTS.md.
/// </summary>
public sealed class PedalsElement : IHudElement
{
    private readonly ArcMeterSettings _brakeSettings;
    private readonly ArcMeterSettings _throttleSettings;

    public PedalsElement(ArcMeterSettings brakeSettings, ArcMeterSettings throttleSettings)
    {
        _brakeSettings = brakeSettings;
        _throttleSettings = throttleSettings;
    }

    public string Id => DefaultElements.Ids.Pedals;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        // Draw in the same inner-to-outer order as the visual specification: boost (layer 0)
        // is rendered by BoostElement, brake (layer 1) sits in the middle, and throttle
        // (layer 2) is the outermost input meter.
        var brakeRadius = ArcMeter.Radius(frame, layer: 1);
        var throttleRadius = ArcMeter.Radius(frame, layer: 2);
        var brakeAngles = ArcMeter.RightAngles(_brakeSettings.ArcLength);
        var throttleAngles = ArcMeter.RightAngles(_throttleSettings.ArcLength);
        var brakeColor = ArcMeter.ResolveColor(_brakeSettings, frame.Theme.Critical);
        var throttleColor = ArcMeter.ResolveColor(_throttleSettings, frame.Theme.Accent);

        ArcMeter.DrawProgress(
            context,
            in frame,
            brakeRadius,
            brakeAngles.StartAngle,
            brakeAngles.SweepAngle,
            frame.Display.Brake,
            MathF.Max(_brakeSettings.StartValue, 0f),
            1f,
            _brakeSettings,
            brakeColor);
        ArcMeter.DrawEndpointMarkers(
            context,
            in frame,
            brakeRadius,
            brakeAngles.StartAngle,
            brakeAngles.SweepAngle,
            _brakeSettings,
            brakeColor);

        ArcMeter.DrawProgress(
            context,
            in frame,
            throttleRadius,
            throttleAngles.StartAngle,
            throttleAngles.SweepAngle,
            frame.Display.Throttle,
            MathF.Max(_throttleSettings.StartValue, 0f),
            1f,
            _throttleSettings,
            throttleColor);
        ArcMeter.DrawEndpointMarkers(
            context,
            in frame,
            throttleRadius,
            throttleAngles.StartAngle,
            throttleAngles.SweepAngle,
            _throttleSettings,
            throttleColor);

        ArcMeter.DrawRightLabel(
            context,
            in frame,
            brakeRadius,
            90f,
            "BRAKE",
            $"{Math.Clamp(frame.Display.Brake, 0f, 1f) * 100f:0}%",
            brakeColor,
            _brakeSettings.LabelOffsetX,
            _brakeSettings.LabelOffsetY);
        ArcMeter.DrawRightLabel(
            context,
            in frame,
            throttleRadius,
            145f,
            "THROTTLE",
            $"{Math.Clamp(frame.Display.Throttle, 0f, 1f) * 100f:0}%",
            throttleColor,
            _throttleSettings.LabelOffsetX,
            _throttleSettings.LabelOffsetY);
    }
}
