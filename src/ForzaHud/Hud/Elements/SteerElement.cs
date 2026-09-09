using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Steering input indicator: a top-mounted arc centred on neutral.
///
/// It shows the steering input FH6 receives from the player's device, not the angle of the
/// front wheels. No understeer, oversteer or rack angle is inferred from it.
/// </summary>
public sealed class SteerElement : IHudElement
{
    private readonly ArcMeterSettings _settings;

    public SteerElement(ArcMeterSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.Steer;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        var radius = ArcMeter.Radius(frame, layer: 0);
        var angles = ArcMeter.TopAngles(_settings.ArcLength);
        var activeColor = ArcMeter.ResolveColor(_settings, frame.Theme.Accent);
        var markerColor = ArcMeter.ResolveColor(_settings, frame.Theme.Primary);
        var steer = Math.Clamp(frame.Display.Steer, -1f, 1f);

        ArcMeter.DrawDirectionalProgress(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            ArcMeter.TopCenterAngle,
            steer,
            _settings,
            activeColor);

        ArcMeter.DrawEndpointMarkers(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            _settings,
            markerColor);
        ArcMeter.DrawMarker(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            0.5f,
            frame.Theme.Dim,
            prominent: true,
            thickness: _settings.Width);
        ArcMeter.DrawMarker(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            (steer + 1f) / 2f,
            markerColor,
            prominent: true,
            thickness: _settings.Width);

        var label = new HudPaint(frame.Theme.Dim, frame.Opacity);
        var labelRadius = radius + 14f * frame.Scale;
        var leftLabel = ArcMeter.PointOnCircle(frame.Origin, labelRadius, angles.StartAngle);
        var rightLabel = ArcMeter.PointOnCircle(
            frame.Origin,
            labelRadius,
            angles.StartAngle + angles.SweepAngle);
        var labelOffset = new HudPoint(
            _settings.LabelOffsetX * frame.Scale,
            _settings.LabelOffsetY * frame.Scale);
        leftLabel += labelOffset;
        rightLabel += labelOffset;
        context.DrawText("L", leftLabel, HudTextStyle.Label, label, HudTextAnchor.Trailing);
        context.DrawText("R", rightLabel, HudTextStyle.Label, label, HudTextAnchor.Leading);
    }
}
