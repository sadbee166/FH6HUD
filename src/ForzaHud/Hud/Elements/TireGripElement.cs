using ForzaHud.Configuration;
using ForzaHud.Rendering;
using ForzaHud.Vehicle;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Grip state of all four tyres, drawn in car layout.
///
/// This communicates states, not raw telemetry: a quiet outline while the tyre is within
/// grip, a coloured outline as it approaches the limit, a solid pad once grip is actually
/// being lost. No "87% grip" figure is shown because FH6 reports slip, not grip.
/// </summary>
public sealed class TireGripElement : IHudElement
{
    private const float PadWidth = 13f;
    private const float PadHeight = 26f;
    private const float ColumnGap = 7f;
    private const float RowGap = 9f;

    private readonly ElementSettings _settings;

    public TireGripElement(ElementSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.Tires;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        var padWidth = PadWidth * frame.Scale;
        var padHeight = PadHeight * frame.Scale;
        var columnGap = ColumnGap * frame.Scale;
        var rowGap = RowGap * frame.Scale;

        var centreX = frame.Origin.X;
        var centreY = frame.Origin.Y;
        var thickness = frame.Visual.LineThickness;

        var leftX = centreX - columnGap / 2f - padWidth;
        var rightX = centreX + columnGap / 2f;
        var frontY = centreY - rowGap / 2f - padHeight;
        var rearY = centreY + rowGap / 2f;

        // Car centre line: orients the pads without drawing a whole car.
        context.DrawLine(
            new HudPoint(centreX, frontY - 6f * frame.Scale),
            new HudPoint(centreX, rearY + padHeight + 6f * frame.Scale),
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.TireCenterLine * frame.Opacity),
            thickness);

        DrawPad(context, frame, leftX, frontY, padWidth, padHeight, frame.State.TireGrip.FrontLeft, thickness);
        DrawPad(context, frame, rightX, frontY, padWidth, padHeight, frame.State.TireGrip.FrontRight, thickness);
        DrawPad(context, frame, leftX, rearY, padWidth, padHeight, frame.State.TireGrip.RearLeft, thickness);
        DrawPad(context, frame, rightX, rearY, padWidth, padHeight, frame.State.TireGrip.RearRight, thickness);
    }

    private static void DrawPad(
        IRenderContext context,
        in HudFrame frame,
        float x,
        float y,
        float width,
        float height,
        TireGripState state,
        float thickness)
    {
        var rect = new HudRect(x, y, width, height);
        var color = frame.Theme.ForGrip(state);
        var hasLostGrip = GripAnalyzer.IsGripLoss(state);

        if (hasLostGrip)
        {
            context.FillRect(rect, new HudPaint(color, frame.Visual.Opacity.TireLostGripFill * frame.Opacity));
        }

        context.DrawRect(
            rect,
            new HudPaint(
                color,
                (state == TireGripState.WithinGrip
                    ? frame.Visual.Opacity.TireWithinGripOutline
                    : 1f)
                * frame.Opacity),
            thickness);
    }
}
