using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Shared geometry for the HUD's arc meters.
///
/// All arcs use the reticle centre as their origin. RPM occupies the left side, steering the
/// top, while the right-side input meters share the same sweep and move outwards by layer:
/// boost, brake, then throttle. Keeping this geometry in one place makes the nesting
/// invariant explicit and lets each element focus on its own value and colour semantics.
/// </summary>
internal static class ArcMeter
{
    /// <summary>Centre angle for the top arc, with increasing values moving clockwise.</summary>
    public const float TopCenterAngle = 0f;

    /// <summary>Centre angle for the left arc, with increasing values moving clockwise.</summary>
    public const float LeftCenterAngle = 270f;

    /// <summary>Centre angle for the right arc, with decreasing values moving clockwise.</summary>
    public const float RightCenterAngle = 90f;

    private const float MinimumRadialSpacing = 8f;
    private const float ActiveThicknessMultiplier = 2.1f;

    /// <summary>Returns a top arc centred on the neutral steering position.</summary>
    public static (float StartAngle, float SweepAngle) TopAngles(float arcLength)
    {
        var length = Math.Clamp(arcLength, 1f, 360f);
        return (TopCenterAngle - length / 2f, length);
    }

    /// <summary>Returns a left-side arc centred on the existing left instrument axis.</summary>
    public static (float StartAngle, float SweepAngle) LeftAngles(float arcLength)
    {
        var length = Math.Clamp(arcLength, 1f, 360f);
        return (LeftCenterAngle - length / 2f, length);
    }

    /// <summary>Returns a right-side arc centred on the existing right instrument axis.</summary>
    public static (float StartAngle, float SweepAngle) RightAngles(float arcLength)
    {
        var length = Math.Clamp(arcLength, 1f, 360f);
        return (RightCenterAngle + length / 2f, -length);
    }

    /// <summary>
    /// Returns the radius for a meter layer. Layer zero is the inner meter; larger layers
    /// are farther from the reticle, so the right-side ordering remains visible.
    /// </summary>
    public static float Radius(in HudFrame frame, int layer)
    {
        var reticleRadius = frame.Visual.ReticleRadius * frame.Reference * frame.Scale;
        var spacing = MathF.Max(MinimumRadialSpacing, frame.Visual.Spacing) * frame.Scale;
        return reticleRadius + spacing * (layer + 1);
    }

    /// <summary>Maps a native-unit value into the meter's normalized progress range.</summary>
    public static float NormalizeValue(float value, float startValue, float endValue)
    {
        if (endValue <= startValue)
        {
            return 0f;
        }

        return Math.Clamp((value - startValue) / (endValue - startValue), 0f, 1f);
    }

    /// <summary>Uses an explicit meter colour or the supplied semantic theme colour.</summary>
    public static HudColor ResolveColor(ArcMeterSettings settings, HudColor semanticColor) =>
        settings.Color ?? semanticColor;

    /// <summary>Draws an inactive track and the active portion of a native-unit meter.</summary>
    public static void DrawProgress(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float startAngle,
        float sweepAngle,
        float value,
        float startValue,
        float endValue,
        ArcMeterSettings settings,
        HudColor activeColor)
    {
        DrawTrack(context, in frame, radius, startAngle, sweepAngle, settings);

        var normalized = NormalizeValue(value, startValue, endValue);
        DrawActive(context, in frame, radius, startAngle, sweepAngle * normalized, settings, activeColor);
    }

    /// <summary>Draws a track and a signed active segment extending from a neutral angle.</summary>
    public static void DrawDirectionalProgress(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float startAngle,
        float sweepAngle,
        float neutralAngle,
        float value,
        ArcMeterSettings settings,
        HudColor activeColor)
    {
        DrawTrack(context, in frame, radius, startAngle, sweepAngle, settings);

        var normalized = Math.Clamp(value, -1f, 1f);
        DrawActive(
            context,
            in frame,
            radius,
            neutralAngle,
            sweepAngle / 2f * normalized,
            settings,
            activeColor);
    }

    private static void DrawTrack(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float startAngle,
        float sweepAngle,
        ArcMeterSettings settings) =>
        context.DrawArc(
            frame.Origin,
            radius,
            radius,
            startAngle,
            sweepAngle,
            new HudPaint(frame.Theme.Dim, frame.Visual.Opacity.ArcTrack * frame.Opacity),
            settings.Width);

    private static void DrawActive(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float startAngle,
        float sweepAngle,
        ArcMeterSettings settings,
        HudColor activeColor)
    {
        if (sweepAngle == 0f)
        {
            return;
        }

        context.DrawArc(
            frame.Origin,
            radius,
            radius,
            startAngle,
            sweepAngle,
            new HudPaint(activeColor, frame.Opacity),
            settings.Width * ActiveThicknessMultiplier);
    }

    /// <summary>Draws a short radial tick at a normalized position on an arc.</summary>
    public static void DrawMarker(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float startAngle,
        float sweepAngle,
        float normalized,
        HudColor color,
        bool prominent,
        float thickness)
    {
        var angle = startAngle + sweepAngle * Math.Clamp(normalized, 0f, 1f);
        var length = (prominent ? 7f : 4f) * frame.Scale;
        var inner = PointOnCircle(frame.Origin, radius - length, angle);
        var outer = PointOnCircle(frame.Origin, radius + length, angle);

        context.DrawLine(
            inner,
            outer,
            new HudPaint(
                color,
                (prominent ? frame.Visual.Opacity.ArcProminentMarker : frame.Visual.Opacity.ArcMarker)
                * frame.Opacity),
            thickness);
    }

    /// <summary>Draws radial markers perpendicular to the curve at both geometric ends.</summary>
    public static void DrawEndpointMarkers(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float startAngle,
        float sweepAngle,
        ArcMeterSettings settings,
        HudColor color)
    {
        DrawMarker(context, in frame, radius, startAngle, sweepAngle, 0f, color, prominent: false, thickness: settings.Width);
        DrawMarker(context, in frame, radius, startAngle, sweepAngle, 1f, color, prominent: false, thickness: settings.Width);
    }

    /// <summary>Returns a point using the renderer's HUD angle convention.</summary>
    public static HudPoint PointOnCircle(HudPoint centre, float radius, float angleDegrees)
    {
        var radians = MathF.PI * angleDegrees / 180f;
        return new HudPoint(
            centre.X + radius * MathF.Sin(radians),
            centre.Y - radius * MathF.Cos(radians));
    }

    /// <summary>Draws a right-side meter label just outside its arc.</summary>
    public static void DrawRightLabel(
        IRenderContext context,
        in HudFrame frame,
        float radius,
        float angle,
        string label,
        string value,
        HudColor valueColor,
        float offsetX = 0f,
        float offsetY = 0f)
    {
        var labelPoint = PointOnCircle(
            frame.Origin,
            radius + 14f * frame.Scale,
            angle);
        labelPoint = new HudPoint(
            labelPoint.X + offsetX * frame.Scale,
            labelPoint.Y + offsetY * frame.Scale);

        context.DrawText(
            label,
            labelPoint,
            HudTextStyle.Label,
            new HudPaint(frame.Theme.Dim, frame.Opacity),
            HudTextAnchor.Leading);

        context.DrawText(
            value,
            new HudPoint(labelPoint.X, labelPoint.Y + 13f * frame.Scale),
            HudTextStyle.Value,
            new HudPaint(valueColor, frame.Opacity),
            HudTextAnchor.Leading,
            HudTextBaseline.Top);
    }
}
