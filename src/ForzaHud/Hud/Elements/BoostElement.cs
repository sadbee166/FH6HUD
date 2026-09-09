using ForzaHud.Configuration;
using ForzaHud.Rendering;
using ForzaHud.Vehicle;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Turbo / supercharger boost as the innermost right-side arc.
///
/// Boost is only interesting on cars that have it, so the element stays hidden until boost
/// outside the deadband has been observed. Naturally aspirated cars simply get no meter
/// rather than a needle that never moves.
/// </summary>
public sealed class BoostElement : IHudElement
{
    private readonly ArcMeterSettings _settings;

    public BoostElement(ArcMeterSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.Boost;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        if (!frame.State.HasBoost)
        {
            return;
        }

        var radius = ArcMeter.Radius(frame, layer: 0);
        var angles = ArcMeter.RightAngles(_settings.ArcLength);
        var activeColor = ArcMeter.ResolveColor(_settings, frame.Theme.Accent);

        ArcMeter.DrawProgress(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            frame.Display.Boost,
            MathF.Max(_settings.StartValue, 0f),
            frame.Display.BoostMaximum,
            _settings,
            activeColor);

        ArcMeter.DrawEndpointMarkers(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            _settings,
            ArcMeter.ResolveColor(_settings, frame.Theme.Accent));

        ArcMeter.DrawRightLabel(
            context,
            in frame,
            radius,
            35f,
            "BOOST",
            frame.Display.Boost.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            frame.Theme.Primary,
            _settings.LabelOffsetX,
            _settings.LabelOffsetY);
    }
}
