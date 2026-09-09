using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Two-dimensional G indicator: a crosshair with a cursor that moves away from centre by
/// lateral and longitudinal acceleration.
///
/// Direction matters as much as magnitude here, which is why it is one compact indicator
/// rather than two numerical gauges. The faint ring is the 1G reference; the outer edge is
/// the configured full scale.
///
/// The cursor is the one element whose smoothing is meant to be tuned - the underlying
/// acceleration is noisy, and an unsmoothed dot is unreadable.
/// </summary>
public sealed class GForceElement : IHudElement
{
    private const float RadiusFraction = 0.062f;
    private const float TickOverhang = 6f;
    private const float CursorRadius = 4.2f;

    private readonly ElementSettings _settings;

    public GForceElement(ElementSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.GForce;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        var radius = RadiusFraction * frame.Reference * frame.Scale;
        var centre = frame.Origin;
        var thickness = frame.Visual.LineThickness;
        var fullScale = frame.GForceFullScale;

        // Outer boundary at full scale.
        context.DrawEllipse(centre, radius, radius,
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.GForceOuterRing * frame.Opacity), thickness);

        // 1G reference ring.
        var referenceRadius = radius / fullScale;
        context.DrawEllipse(centre, referenceRadius, referenceRadius,
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.GForceReferenceRing * frame.Opacity), thickness);

        // Crosshair.
        var overhang = TickOverhang * frame.Scale;
        context.DrawLine(
            new HudPoint(centre.X - radius - overhang, centre.Y),
            new HudPoint(centre.X + radius + overhang, centre.Y),
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.GForceCrosshair * frame.Opacity), thickness);
        context.DrawLine(
            new HudPoint(centre.X, centre.Y - radius - overhang),
            new HudPoint(centre.X, centre.Y + radius + overhang),
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.GForceCrosshair * frame.Opacity), thickness);

        var lateral = frame.Display.LateralG / fullScale;
        var longitudinal = frame.Display.LongitudinalG / fullScale;
        var magnitude = MathF.Sqrt((lateral * lateral) + (longitudinal * longitudinal));

        var cursor = new HudPoint(
            centre.X + Math.Clamp(lateral, -1f, 1f) * radius,
            centre.Y - Math.Clamp(longitudinal, -1f, 1f) * radius);

        // Trace from centre: makes small deflections readable at a glance.
        context.DrawLine(
            centre, cursor,
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.GForceTrace * frame.Opacity), thickness);

        var color = magnitude >= 0.95f ? frame.Theme.Critical
            : magnitude >= (1f / fullScale) ? frame.Theme.Warning
            : frame.Theme.Accent;

        context.FillEllipse(
            cursor,
            CursorRadius * frame.Scale,
            CursorRadius * frame.Scale,
            new HudPaint(color, frame.Opacity));
    }
}
