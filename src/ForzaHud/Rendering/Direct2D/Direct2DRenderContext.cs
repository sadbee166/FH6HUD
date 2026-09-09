using System.Numerics;
using ForzaHud.Configuration;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ForzaHud.Rendering.Direct2D;

/// <summary>
/// Direct2D / DirectWrite implementation of the HUD drawing API.
///
/// This is the only type in the application that knows about Direct2D. Everything else
/// draws through <see cref="IRenderContext"/>.
///
/// It works with any <see cref="ID2D1RenderTarget"/>: DirectComposition uses a swap-chain
/// bitmap target, and offline verification uses a WIC bitmap target.
/// </summary>
public sealed class Direct2DRenderContext : ITransformableRenderContext, IDepthTransformableRenderContext, IDisposable
{
    private const float ArcSegmentDegrees = 3f;

    private readonly ID2D1Factory _factory;
    private readonly ID2D1RenderTarget _target;
    private readonly ID2D1DeviceContext? _deviceContext;
    private readonly ID2D1Image? _mainTarget;
    private readonly IDWriteFactory _textFactory;
    private readonly VisualSettings _visual;

    private readonly Dictionary<HudTextStyle, IDWriteTextFormat> _textFormats = [];
    private readonly Dictionary<(byte R, byte G, byte B, byte A), ID2D1SolidColorBrush> _brushes = [];

    public Direct2DRenderContext(
        ID2D1Factory factory,
        ID2D1RenderTarget target,
        IDWriteFactory textFactory,
        VisualSettings visual,
        ID2D1DeviceContext? deviceContext = null,
        ID2D1Image? mainTarget = null)
    {
        _factory = factory;
        _target = target;
        _deviceContext = deviceContext;
        _mainTarget = mainTarget;
        _textFactory = textFactory;
        _visual = visual;

        target.AntialiasMode = AntialiasMode.PerPrimitive;
        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;

        _textFormats[HudTextStyle.Label] = CreateTextFormat(visual.Typography.LabelSize, FontWeight.Regular);
        _textFormats[HudTextStyle.Value] = CreateTextFormat(visual.Typography.ValueSize, FontWeight.SemiBold);
        _textFormats[HudTextStyle.Gear] = CreateTextFormat(visual.Typography.GearSize, FontWeight.SemiBold);
        _textFormats[HudTextStyle.Primary] = CreateTextFormat(visual.Typography.PrimarySize, FontWeight.SemiBold);
    }

    public float Width => _target.Size.Width;

    public float Height => _target.Size.Height;

    /// <summary>Prepares the target for a new frame with a fully transparent background.</summary>
    public void BeginDraw()
    {
        _target.BeginDraw();
        _target.Clear(new Color4(0f, 0f, 0f, 0f));
    }

    /// <summary>Finishes the frame and presents it.</summary>
    public void EndDraw() => _target.EndDraw().CheckError();

    public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) =>
        _target.DrawLine(ToVector2(from), ToVector2(to), Brush(paint), thickness);

    public void DrawArc(
        HudPoint center,
        float radiusX,
        float radiusY,
        float startAngle,
        float sweepAngle,
        HudPaint paint,
        float thickness)
    {
        var brush = Brush(paint);
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepAngle) / ArcSegmentDegrees));

        var previous = PointOnEllipse(center, radiusX, radiusY, startAngle);
        for (var i = 1; i <= steps; i++)
        {
            var angle = startAngle + (sweepAngle * i / steps);
            var current = PointOnEllipse(center, radiusX, radiusY, angle);
            _target.DrawLine(ToVector2(previous), ToVector2(current), brush, thickness);
            previous = current;
        }
    }

    public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness) =>
        _target.DrawEllipse(new Ellipse(ToVector2(center), radiusX, radiusY), Brush(paint), thickness);

    public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint) =>
        _target.FillEllipse(new Ellipse(ToVector2(center), radiusX, radiusY), Brush(paint));

    public void DrawRect(HudRect rect, HudPaint paint, float thickness) =>
        _target.DrawRectangle(ToRect(rect), Brush(paint), thickness);

    public void FillRect(HudRect rect, HudPaint paint) =>
        _target.FillRectangle(ToRect(rect), Brush(paint));

    public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint)
    {
        if (points.Length < 3)
        {
            return;
        }

        using var geometry = _factory.CreatePathGeometry();
        using var sink = geometry.Open();

        sink.BeginFigure(ToVector2(points[0]), FigureBegin.Filled);
        for (var i = 1; i < points.Length; i++)
        {
            sink.AddLine(ToVector2(points[i]));
        }

        sink.EndFigure(FigureEnd.Closed);
        sink.Close();

        _target.FillGeometry(geometry, Brush(paint));
    }

    public void DrawText(
        string text,
        HudPoint position,
        HudTextStyle style,
        HudPaint paint,
        HudTextAnchor anchor = HudTextAnchor.Center,
        HudTextBaseline baseline = HudTextBaseline.Middle)
    {
        using var layout = CreateLayout(text, style);
        var metrics = layout.Metrics;

        var origin = new Vector2(
            anchor switch
            {
                HudTextAnchor.Leading => position.X,
                HudTextAnchor.Trailing => position.X - metrics.Width,
                _ => position.X - metrics.Width / 2f,
            },
            baseline switch
            {
                HudTextBaseline.Top => position.Y,
                HudTextBaseline.Bottom => position.Y - metrics.Height,
                _ => position.Y - metrics.Height / 2f,
            });

        _target.DrawTextLayout(origin, layout, Brush(paint));
    }

    public HudSize MeasureText(string text, HudTextStyle style)
    {
        using var layout = CreateLayout(text, style);
        return new HudSize(layout.Metrics.Width, layout.Metrics.Height);
    }

    public IDisposable PushTransform(HudTransform transform)
    {
        var previous = _target.Transform;
        var anchor = new Vector2(transform.Anchor.X, transform.Anchor.Y);
        var radians = MathF.PI * transform.RotationDegrees / 180f;
        var local = Matrix3x2.CreateScale(transform.Scale, anchor)
            * Matrix3x2.CreateRotation(radians, anchor)
            * Matrix3x2.CreateTranslation(transform.Offset.X, transform.Offset.Y);

        _target.Transform = local * previous;
        return new TransformScope(_target, previous);
    }

    public IDisposable PushDepthTransform(HudDepthTransform transform)
    {
        if (transform.IsIdentity)
        {
            return NoOpScope.Instance;
        }

        if (_deviceContext is not null && _mainTarget is not null)
        {
            var pixelSize = _deviceContext.PixelSize;
            var dpi = _deviceContext.Dpi;
            var bitmap = _deviceContext.CreateBitmap(
                pixelSize,
                new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(
                        Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        Vortice.DCommon.AlphaMode.Premultiplied),
                    dpi.Width,
                    dpi.Height,
                    BitmapOptions.Target));
            var previousTransform = _deviceContext.Transform;

            _deviceContext.Target = bitmap;
            _deviceContext.Transform = previousTransform;
            _deviceContext.Clear(new Color4(0f, 0f, 0f, 0f));

            return new DepthTransformScope(
                _deviceContext,
                _mainTarget,
                bitmap,
                previousTransform,
                transform);
        }

        // WIC snapshots use ID2D1RenderTarget. Their
        // affine fallback preserves the expected foreshortening without claiming perspective.
        var previous = _target.Transform;
        var scale = Matrix3x2.CreateScale(
            MathF.Cos(MathF.PI * transform.YawDegrees / 180f),
            MathF.Cos(MathF.PI * transform.PitchDegrees / 180f),
            new Vector2(transform.Pivot.X, transform.Pivot.Y));
        _target.Transform = scale * previous;
        return new TransformScope(_target, previous);
    }

    public void Dispose()
    {
        foreach (var format in _textFormats.Values)
        {
            format.Dispose();
        }

        _textFormats.Clear();

        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();
    }

    /// <summary>
    /// Point on an ellipse. Zero degrees is straight up and positive angles sweep clockwise.
    /// </summary>
    private static HudPoint PointOnEllipse(HudPoint center, float radiusX, float radiusY, float angleDegrees)
    {
        var radians = MathF.PI * angleDegrees / 180f;
        return new HudPoint(
            center.X + radiusX * MathF.Sin(radians),
            center.Y - radiusY * MathF.Cos(radians));
    }

    private IDWriteTextFormat CreateTextFormat(float size, FontWeight weight) =>
        _textFactory.CreateTextFormat(_visual.FontFamily, weight, FontStyle.Normal, FontStretch.Normal, size);

    private IDWriteTextLayout CreateLayout(string text, HudTextStyle style) =>
        _textFactory.CreateTextLayout(text, _textFormats[style], float.MaxValue, float.MaxValue);

    private ID2D1SolidColorBrush Brush(HudPaint paint)
    {
        var effectiveAlpha = paint.EffectiveAlpha;
        var key = (paint.Color.R, paint.Color.G, paint.Color.B, effectiveAlpha);

        if (_brushes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var brush = _target.CreateSolidColorBrush(
            new Color4(
                paint.Color.R / 255f,
                paint.Color.G / 255f,
                paint.Color.B / 255f,
                effectiveAlpha / 255f),
            null);

        _brushes[key] = brush;
        return brush;
    }

    private static Vector2 ToVector2(HudPoint point) => new(point.X, point.Y);

    private static Rect ToRect(HudRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private sealed class TransformScope(ID2D1RenderTarget target, Matrix3x2 previous) : IDisposable
    {
        private ID2D1RenderTarget? _target = target;

        public void Dispose()
        {
            _target?.Transform = previous;
            _target = null;
        }
    }

    private sealed class DepthTransformScope(
        ID2D1DeviceContext context,
        ID2D1Image mainTarget,
        ID2D1Bitmap1 bitmap,
        Matrix3x2 previousTransform,
        HudDepthTransform transform) : IDisposable
    {
        private ID2D1DeviceContext? _context = context;
        private ID2D1Image? _mainTarget = mainTarget;
        private ID2D1Bitmap1? _bitmap = bitmap;

        public void Dispose()
        {
            var context = _context;
            if (context is null)
            {
                return;
            }

            try
            {
                context.Target = _mainTarget;
                context.Transform = Matrix3x2.Identity;
                var matrix = transform.ToPerspectiveMatrix();
                context.DrawBitmap(_bitmap!, 1f, InterpolationMode.Linear, in matrix);
            }
            finally
            {
                context.Transform = previousTransform;
                _bitmap?.Dispose();
                _context = null;
                _mainTarget = null;
                _bitmap = null;
            }
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static NoOpScope Instance { get; } = new();

        public void Dispose() { }
    }
}
