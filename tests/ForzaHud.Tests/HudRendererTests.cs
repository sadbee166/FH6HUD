using ForzaHud.Configuration;
using ForzaHud.Hud;
using ForzaHud.Rendering;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class HudRendererTests
{
    [Fact]
    public void HideWhenNotDrivingSkipsAllDrawingForNonDrivingState()
    {
        var configuration = new HudConfiguration();
        configuration.Overlay.HideWhenNotDriving = true;
        var context = new RecordingRenderContext();
        var state = new DerivedState { IsDriving = false };

        new HudRenderer(configuration).Draw(context, new HudEngine(configuration), state, hasTelemetry: true);

        Assert.Equal(0, context.DrawCallCount);
    }

    [Fact]
    public void HideWhenNotDrivingStillDrawsWhileDriving()
    {
        var configuration = new HudConfiguration();
        configuration.Overlay.HideWhenNotDriving = true;
        var context = new RecordingRenderContext();
        var state = new DerivedState { IsDriving = true };

        new HudRenderer(configuration).Draw(context, new HudEngine(configuration), state, hasTelemetry: true);

        Assert.True(context.DrawCallCount > 0);
    }

    [Fact]
    public void CalibrationIndicatorDrawsEvenWhenHudIsHiddenWhileNotDriving()
    {
        var configuration = new HudConfiguration();
        configuration.Overlay.HideWhenNotDriving = true;
        var context = new RecordingRenderContext();

        new HudRenderer(configuration).Draw(
            context,
            new HudEngine(configuration),
            new DerivedState { IsDriving = false },
            hasTelemetry: false,
            isCalibrationRecording: true);

        Assert.Contains("RPM CALIBRATION", context.Texts);
    }

    [Fact]
    public void CalibrationIndicatorDoesNotDrawWhenRecorderIsInactive()
    {
        var configuration = new HudConfiguration();
        var context = new RecordingRenderContext();

        new HudRenderer(configuration).Draw(
            context,
            new HudEngine(configuration),
            new DerivedState { IsDriving = true },
            hasTelemetry: true,
            isCalibrationRecording: false);

        Assert.DoesNotContain("RPM CALIBRATION", context.Texts);
    }

    [Fact]
    public void ElementOpacityScalesThemeAlpha()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.Theme.Primary = new HudColor(255, 255, 255, 200);
        configuration.Visual.Theme.Dim = new HudColor(255, 255, 255, 200);
        foreach (var id in configuration.Elements.Keys.ToArray())
        {
            configuration.Elements[id] = configuration.Elements[id] with
            {
                Enabled = id == DefaultElements.Ids.Speed,
            };
        }

        configuration.Elements[DefaultElements.Ids.Speed] = new ElementSettings(
            Enabled: true,
            X: 0.5,
            Y: 0.5,
            Scale: 1.0,
            Opacity: 0.5);

        var context = new RecordingRenderContext();

        new HudRenderer(configuration).Draw(
            context,
            new HudEngine(configuration),
            new DerivedState { IsDriving = true },
            hasTelemetry: true);

        Assert.Equal(2, context.Paints.Count);
        Assert.All(context.Paints, paint =>
        {
            Assert.Equal(0.5f, paint.Opacity);
            Assert.Equal(100, paint.EffectiveAlpha);
        });
    }

    [Fact]
    public void FrameTcsDetectionZoneDrawsOnlyWhenEnabledInFrameMode()
    {
        var configuration = new HudConfiguration();
        configuration.Telemetry.TractionControl.DetectionMode = TcsDetectionMode.Frame;
        configuration.Telemetry.TractionControl.Frame.ShowDetectionZone = true;
        foreach (var id in configuration.Elements.Keys.ToArray())
        {
            configuration.Elements[id] = configuration.Elements[id] with { Enabled = false };
        }

        var context = new RecordingRenderContext();

        new HudRenderer(configuration).Draw(
            context,
            new HudEngine(configuration),
            new DerivedState(),
            hasTelemetry: false);

        var rect = Assert.Single(context.Rectangles);
        Assert.Equal(1516.8f, rect.X);
        Assert.Equal(885.6f, rect.Y);
        Assert.Equal(384f, rect.Width);
        Assert.Equal(151.2f, rect.Height, precision: 3);

        configuration.Telemetry.TractionControl.DetectionMode = TcsDetectionMode.Telemetry;
        context = new RecordingRenderContext();
        new HudRenderer(configuration).Draw(
            context,
            new HudEngine(configuration),
            new DerivedState(),
            hasTelemetry: false);

        Assert.Empty(context.Rectangles);

        configuration.Telemetry.TractionControl.Enabled = false;
        configuration.Telemetry.TractionControl.DetectionMode = TcsDetectionMode.Frame;
        context = new RecordingRenderContext();
        new HudRenderer(configuration).Draw(
            context,
            new HudEngine(configuration),
            new DerivedState(),
            hasTelemetry: false);

        Assert.Empty(context.Rectangles);
    }

    private sealed class RecordingRenderContext : IRenderContext
    {
        public float Width => 1920;

        public float Height => 1080;

        public int DrawCallCount { get; private set; }

        public List<string> Texts { get; } = [];

        public List<HudPaint> Paints { get; } = [];

        public List<HudRect> Rectangles { get; } = [];

        public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) => DrawCallCount++;

        public void DrawArc(HudPoint center, float radiusX, float radiusY, float startAngle, float sweepAngle, HudPaint paint, float thickness) => DrawCallCount++;

        public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness) => DrawCallCount++;

        public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint) => DrawCallCount++;

        public void DrawRect(HudRect rect, HudPaint paint, float thickness)
        {
            DrawCallCount++;
            Rectangles.Add(rect);
        }

        public void FillRect(HudRect rect, HudPaint paint) => DrawCallCount++;

        public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint) => DrawCallCount++;

        public void DrawText(
            string text,
            HudPoint position,
            HudTextStyle style,
            HudPaint paint,
            HudTextAnchor anchor = HudTextAnchor.Center,
            HudTextBaseline baseline = HudTextBaseline.Middle)
        {
            DrawCallCount++;
            Texts.Add(text);
            Paints.Add(paint);
        }

        public HudSize MeasureText(string text, HudTextStyle style) => new(100, 20);
    }
}
