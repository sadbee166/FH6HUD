using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud;

/// <summary>Shows the screen region used by the Frame-mode TCS detector.</summary>
internal static class FrameTcsDebugOverlay
{
    public static void Draw(
        IRenderContext context,
        FrameTcsSettings settings,
        HudTheme theme,
        VisualSettings visual,
        float opacity)
    {
        var region = new HudRect(
            settings.RegionX * context.Width,
            settings.RegionY * context.Height,
            settings.RegionWidth * context.Width,
            settings.RegionHeight * context.Height);

        context.DrawRect(
            region,
            new HudPaint(theme.Accent, opacity),
            visual.LineThickness);
    }
}
