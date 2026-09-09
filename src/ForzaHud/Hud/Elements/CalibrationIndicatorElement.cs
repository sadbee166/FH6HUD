using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>Shows that the RPM calibration recorder is actively collecting telemetry.</summary>
public sealed class CalibrationIndicatorElement : IHudElement
{
    public string Id => DefaultElements.Ids.Calibration;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        context.DrawText(
            "RPM CALIBRATION",
            frame.Origin,
            HudTextStyle.Label,
            new HudPaint(frame.Theme.Critical, frame.Opacity));
    }
}
