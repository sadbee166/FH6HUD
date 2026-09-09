using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Current gear. A single glyph: gear changes are discrete events and are never smoothed.
/// </summary>
public sealed class GearElement : IHudElement
{
    private readonly ElementSettings _settings;

    public GearElement(ElementSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.Gear;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        context.DrawText(
            frame.State.GearLabel,
            frame.Origin,
            HudTextStyle.Gear,
            new HudPaint(frame.Theme.Primary, frame.Opacity));
    }
}
