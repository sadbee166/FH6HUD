namespace ForzaHud.Rendering;

/// <summary>
/// The drawing API HUD elements are written against.
///
/// Deliberately small: lines, arcs, circles, rectangles, polygons and text. Elements never
/// see Direct2D, DirectWrite, device contexts or fonts, so the renderer underneath can
/// change without touching HUD behaviour, and HUD geometry can be exercised without a
/// Windows window at all.
///
/// Angles are in degrees. Zero is straight up (12 o'clock) and positive angles sweep
/// clockwise, which is the convention a rev counter wants.
/// </summary>
public interface IRenderContext
{
    /// <summary>Drawable width in device-independent pixels.</summary>
    float Width { get; }

    /// <summary>Drawable height in device-independent pixels.</summary>
    float Height { get; }

    /// <summary>Draws a straight line between two points.</summary>
#pragma warning disable CA1716 // "to" reads better than any synonym here.
    void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness);
#pragma warning restore CA1716

    /// <summary>Draws an elliptical arc, or a full ellipse when the sweep is 360 degrees.</summary>
    void DrawArc(HudPoint center, float radiusX, float radiusY, float startAngle, float sweepAngle, HudPaint paint, float thickness);

    /// <summary>Draws the outline of an axis-aligned ellipse.</summary>
    void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness);

    /// <summary>Fills an axis-aligned ellipse.</summary>
    void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint);

    /// <summary>Draws the outline of a rectangle.</summary>
    void DrawRect(HudRect rect, HudPaint paint, float thickness);

    /// <summary>Fills a rectangle.</summary>
    void FillRect(HudRect rect, HudPaint paint);

    /// <summary>Fills a closed polygon. Used for solid indicators such as tyre pads.</summary>
    void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint);

    /// <summary>Draws one line of text.</summary>
    void DrawText(
        string text,
        HudPoint position,
        HudTextStyle style,
        HudPaint paint,
        HudTextAnchor anchor = HudTextAnchor.Center,
        HudTextBaseline baseline = HudTextBaseline.Middle);

    /// <summary>Measures one line of text in device-independent pixels.</summary>
    HudSize MeasureText(string text, HudTextStyle style);
}

/// <summary>
/// Optional render-context capability for applying an affine transform to a complete draw
/// group, including text and stroke widths.
/// </summary>
public interface ITransformableRenderContext : IRenderContext
{
    IDisposable PushTransform(HudTransform transform);
}

/// <summary>Optional render-context capability for a complete panel rotation into screen depth.</summary>
public interface IDepthTransformableRenderContext : IRenderContext
{
    IDisposable PushDepthTransform(HudDepthTransform transform);
}
