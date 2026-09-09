using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Road speed, the dominant HUD element. It has to be readable without looking directly at
/// it, so it is drawn as a plain numeral with a small unit caption and no decoration.
/// </summary>
public sealed class SpeedElement : IHudElement
{
    private readonly ElementSettings _settings;

    public SpeedElement(ElementSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.Speed;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        var speed = (int)Math.Clamp(frame.Display.Speed, 0f, 9999f);
        var text = speed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        context.DrawText(
            text,
            frame.Origin,
            HudTextStyle.Primary,
            new HudPaint(frame.Theme.Primary, frame.Opacity));

        var captionOffset = (frame.Visual.Typography.PrimarySize * 0.62f) * frame.Scale;
        context.DrawText(
            frame.State.SpeedUnitLabel,
            new HudPoint(frame.Origin.X, frame.Origin.Y + captionOffset),
            HudTextStyle.Label,
            new HudPaint(frame.Theme.Dim, frame.Opacity));
    }
}
