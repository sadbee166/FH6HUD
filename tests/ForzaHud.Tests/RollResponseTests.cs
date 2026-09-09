using System.Buffers.Binary;
using ForzaHud.Configuration;
using ForzaHud.Hud;
using ForzaHud.Hud.Elements;
using ForzaHud.Rendering;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class RollResponseTests
{
    [Fact]
    public void ParserDecodesRollAtOffset64()
    {
        var buffer = new byte[ForzaPacketFormat.PacketSize];
        const float expectedRoll = 0.35f;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(ForzaPacketFormat.Sled.Roll, 4), expectedRoll);

        var success = ForzaPacketParser.TryParse(buffer, TimeSpan.Zero, out var snapshot);

        Assert.True(success);
        Assert.Equal(expectedRoll, snapshot.Roll);
    }

    [Fact]
    public void VehicleStateProcessorConvertsRollToDegrees()
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());
        const float rollRadians = 0.5f;
        var snapshot = TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            Roll = rollRadians,
        };

        var state = processor.Process(snapshot);

        var expectedDegrees = rollRadians * (180f / MathF.PI);
        Assert.Equal(expectedDegrees, state.RollDegrees, precision: 4);
    }

    [Fact]
    public void HudEngineSmoothsAndResetsPanelMotionRoll()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.PanelMotion.SmoothingMilliseconds = 100;
        var engine = new HudEngine(configuration);

        // First frame initializes baseline at 0 degrees
        engine.Update(new DerivedState { RollDegrees = 0f }, deltaSeconds: 0.016);
        Assert.Equal(0f, engine.Display.PanelMotionRollDegrees);

        // Second frame moves toward 10 degrees with exponential smoothing
        var state1 = new DerivedState { RollDegrees = 10f };
        engine.Update(state1, deltaSeconds: 0.05);

        // After 50ms with a 100ms time constant, value should move toward 10 but be less than 10.
        Assert.True(engine.Display.PanelMotionRollDegrees > 0f);
        Assert.True(engine.Display.PanelMotionRollDegrees < 10f);

        // Reset should reset smoothing history so next frame uses target with no lag from previous value
        var resetState = new DerivedState { RollDegrees = -15f };
        engine.Reset(resetState);
        engine.Update(resetState, deltaSeconds: 0.05);
        Assert.Equal(-15f, engine.Display.PanelMotionRollDegrees);
    }

    [Theory]
    [InlineData(-1f, 10f, -10f)]
    [InlineData(1f, 10f, 10f)]
    [InlineData(0f, 10f, 0f)]
    public void PanelMotionCalculatorAppliesRollMultiplier(float multiplier, float inputRoll, float expectedRotation)
    {
        var settings = new PanelMotionSettings
        {
            Enabled = true,
            RollMultiplier = multiplier,
            SpeedShake = new SpeedShakeSettings { Enabled = false },
        };
        var display = new HudDisplay
        {
            PanelMotionRollDegrees = inputRoll,
        };

        var motion = PanelMotionCalculator.Calculate(display, settings, reference: 1000f);

        Assert.Equal(expectedRotation, motion.RotationDegrees);
    }

    [Fact]
    public void PanelMotionCalculatorReturnsIdentityWhenDisabled()
    {
        var settings = new PanelMotionSettings
        {
            Enabled = false,
            RollMultiplier = -1f,
        };
        var display = new HudDisplay
        {
            PanelMotionRollDegrees = 25f,
            PanelMotionLateralG = 1.5f,
            PanelMotionLongitudinalG = 1.0f,
        };

        var motion = PanelMotionCalculator.Calculate(display, settings, reference: 1000f);

        Assert.Equal(PanelMotion.Identity, motion);
        Assert.Equal(0f, motion.RotationDegrees);
    }

    [Fact]
    public void PanelMotionApplyRotatesPointsAroundAnchor()
    {
        var anchor = new HudPoint(500f, 500f);
        var pointRight = new HudPoint(600f, 500f); // 100px to the right

        // 90 degrees clockwise -> point goes 100px down from anchor
        var motion90 = new PanelMotion(Scale: 1f, Offset: new HudPoint(0f, 0f), RotationDegrees: 90f);
        var rotated90 = motion90.Apply(pointRight, anchor);
        Assert.Equal(500f, rotated90.X, precision: 3);
        Assert.Equal(600f, rotated90.Y, precision: 3);

        // -90 degrees counter-clockwise -> point goes 100px up from anchor
        var motionNeg90 = new PanelMotion(Scale: 1f, Offset: new HudPoint(0f, 0f), RotationDegrees: -90f);
        var rotatedNeg90 = motionNeg90.Apply(pointRight, anchor);
        Assert.Equal(500f, rotatedNeg90.X, precision: 3);
        Assert.Equal(400f, rotatedNeg90.Y, precision: 3);

        // 180 degrees -> point goes 100px left of anchor
        var motion180 = new PanelMotion(Scale: 1f, Offset: new HudPoint(0f, 0f), RotationDegrees: 180f);
        var rotated180 = motion180.Apply(pointRight, anchor);
        Assert.Equal(400f, rotated180.X, precision: 3);
        Assert.Equal(500f, rotated180.Y, precision: 3);
    }

    [Fact]
    public void ReticleElementDrawsExactUnrotatedMarkerEndpoints()
    {
        var configuration = new HudConfiguration();
        var visual = configuration.Visual;
        visual.ReticleRadius = 0.30f;
        visual.ReticleLevelMarkerLength = 0.10f;
        visual.LineThickness = 2.0f;

        var element = new ReticleElement(visual.Meters.Rpm);

        var context = new DetailedRecordingRenderContext();
        var theme = HudTheme.From(visual.Theme);
        const float width = 1000f;
        const float height = 1000f;
        var origin = new HudPoint(500f, 500f);

        var frame = new HudFrame(
            State: new DerivedState { IsDriving = true },
            Display: new HudDisplay(),
            Visual: visual,
            Theme: theme,
            Origin: origin,
            Scale: 1f,
            Opacity: 1f,
            Width: width,
            Height: height,
            GForceFullScale: 1.5f);

        element.Draw(context, in frame);

        // Radius = 0.30 * 1000 * 1 = 300px
        // MarkerLength = 300 * 0.10 = 30px
        // 9 o'clock marker: from (200, 500) to (230, 500)
        // 3 o'clock marker: from (800, 500) to (770, 500)
        var lines = context.Lines;
        Assert.Equal(2, lines.Count);

        var leftLine = lines.FirstOrDefault(l => Math.Abs(l.From.X - 200f) < 0.01f);
        Assert.NotNull(leftLine);
        Assert.Equal(500f, leftLine.From.Y);
        Assert.Equal(230f, leftLine.To.X);
        Assert.Equal(500f, leftLine.To.Y);

        var rightLine = lines.FirstOrDefault(l => Math.Abs(l.From.X - 800f) < 0.01f);
        Assert.NotNull(rightLine);
        Assert.Equal(500f, rightLine.From.Y);
        Assert.Equal(770f, rightLine.To.X);
        Assert.Equal(500f, rightLine.To.Y);
    }

    [Fact]
    public void ReticleElementCounterRotatesMarkersRelativeToPanel()
    {
        var configuration = new HudConfiguration();
        var visual = configuration.Visual;
        visual.ReticleRadius = 0.30f;
        visual.ReticleLevelMarkerLength = 0.10f;
        visual.LineThickness = 2.0f;
        visual.PanelMotion.Enabled = true;
        visual.PanelMotion.RollMultiplier = -1f;

        var element = new ReticleElement(visual.Meters.Rpm);

        var context = new DetailedRecordingRenderContext();
        var theme = HudTheme.From(visual.Theme);
        const float width = 1000f;
        const float height = 1000f;
        var origin = new HudPoint(500f, 500f);

        const float rollDegrees = 30f;
        var display = new HudDisplay
        {
            PanelMotionRollDegrees = rollDegrees,
        };

        var frame = new HudFrame(
            State: new DerivedState { IsDriving = true },
            Display: display,
            Visual: visual,
            Theme: theme,
            Origin: origin,
            Scale: 1f,
            Opacity: 1f,
            Width: width,
            Height: height,
            GForceFullScale: 1.5f);

        element.Draw(context, in frame);

        var lines = context.Lines;
        Assert.Equal(2, lines.Count);

        // Panel rotation is -30 deg, so markers counter-rotate by +30 deg in local element space.
        // Applying the panel rotation (-30 deg) to the drawn local lines must yield perfectly horizontal lines in monitor space (Y == 500f).
        var panelMotion = new PanelMotion(Scale: 1f, Offset: new HudPoint(0f, 0f), RotationDegrees: -30f);

        foreach (var line in lines)
        {
            var screenFrom = panelMotion.Apply(line.From, origin);
            var screenTo = panelMotion.Apply(line.To, origin);

            Assert.Equal(500f, screenFrom.Y, precision: 3);
            Assert.Equal(500f, screenTo.Y, precision: 3);
        }
    }

    [Fact]
    public void HudRendererPassesRotationDegreesInTransform()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.PanelMotion.Enabled = true;
        configuration.Visual.PanelMotion.RollMultiplier = -1f;
        configuration.Visual.PanelMotion.SpeedShake.Enabled = false;
        configuration.Visual.PanelMotion.LevelSpeedAndGear = false;

        var renderer = new HudRenderer(configuration);
        var engine = new HudEngine(configuration);
        engine.Display.PanelMotionRollDegrees = 8f;

        var context = new TransformTrackingRenderContext();
        var state = new DerivedState { IsDriving = true };

        renderer.Draw(context, engine, state, hasTelemetry: true);

        Assert.Single(context.Transforms);
        Assert.Equal(-8f, context.Transforms[0].RotationDegrees);
        Assert.Equal(new HudPoint(960f, 540f), context.Transforms[0].Anchor);
    }

    [Fact]
    public void HudRendererPushesCounterTransformForGearAndSpeedWhenLevelSpeedAndGearEnabled()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.PanelMotion.Enabled = true;
        configuration.Visual.PanelMotion.RollMultiplier = -1f;
        configuration.Visual.PanelMotion.SpeedShake.Enabled = false;
        configuration.Visual.PanelMotion.LevelSpeedAndGear = true;

        var renderer = new HudRenderer(configuration);
        var engine = new HudEngine(configuration);
        engine.Display.PanelMotionRollDegrees = 12f;

        var context = new TransformTrackingRenderContext();
        var state = new DerivedState { IsDriving = true };

        renderer.Draw(context, engine, state, hasTelemetry: true);

        // Expect 3 transforms: outer panel (-12 deg), gear counter-rotation (+12 deg), speed counter-rotation (+12 deg)
        Assert.Equal(3, context.Transforms.Count);
        Assert.Equal(-12f, context.Transforms[0].RotationDegrees);

        var innerTransforms = context.Transforms.Skip(1).ToList();
        Assert.All(innerTransforms, t =>
        {
            Assert.Equal(12f, t.RotationDegrees);
            Assert.Equal(new HudPoint(960f, 540f), t.Anchor);
            Assert.Equal(1f, t.Scale);
        });
    }

    [Fact]
    public void HudRendererKeepsGearAndSpeedOriginsLevelInNonTransformContext()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.PanelMotion.Enabled = true;
        configuration.Visual.PanelMotion.RollMultiplier = -1f;
        configuration.Visual.PanelMotion.SpeedShake.Enabled = false;
        configuration.Visual.PanelMotion.LevelSpeedAndGear = true;

        var renderer = new HudRenderer(configuration);
        var engine = new HudEngine(configuration);
        engine.Display.PanelMotionRollDegrees = 20f;
        engine.Display.Speed = 100f;

        var context = new PositionRecordingRenderContext();
        var state = new DerivedState { IsDriving = true, GearLabel = "4" };

        renderer.Draw(context, engine, state, hasTelemetry: true);

        // Gear and speed text must be drawn at Y = 540 (horizontal midline)
        var gearCall = context.TextPositions.FirstOrDefault(t => t.Text == "4");
        Assert.NotNull(gearCall);
        Assert.Equal(540f, gearCall.Position.Y, precision: 3);

        var speedCall = context.TextPositions.FirstOrDefault(t => t.Text == "100");
        Assert.NotNull(speedCall);
        Assert.Equal(540f, speedCall.Position.Y, precision: 3);
    }

    private sealed record DrawnLine(HudPoint From, HudPoint To, HudPaint Paint, float Thickness);
    private sealed record DrawnText(string Text, HudPoint Position);

    private sealed class DetailedRecordingRenderContext : IRenderContext
    {
        public float Width => 1000;
        public float Height => 1000;

        public List<DrawnLine> Lines { get; } = [];
        public List<(HudPoint Center, float RadiusX, float RadiusY, float StartAngle, float SweepAngle)> Arcs { get; } = [];

        public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) =>
            Lines.Add(new DrawnLine(from, to, paint, thickness));

        public void DrawArc(HudPoint center, float radiusX, float radiusY, float startAngle, float sweepAngle, HudPaint paint, float thickness) =>
            Arcs.Add((center, radiusX, radiusY, startAngle, sweepAngle));

        public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness) { }
        public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint) { }
        public void DrawRect(HudRect rect, HudPaint paint, float thickness) { }
        public void FillRect(HudRect rect, HudPaint paint) { }
        public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint) { }
        public void DrawText(string text, HudPoint position, HudTextStyle style, HudPaint paint, HudTextAnchor anchor = HudTextAnchor.Center, HudTextBaseline baseline = HudTextBaseline.Middle) { }
        public HudSize MeasureText(string text, HudTextStyle style) => new(100, 20);
    }

    private sealed class PositionRecordingRenderContext : IRenderContext
    {
        public float Width => 1920;
        public float Height => 1080;

        public List<DrawnText> TextPositions { get; } = [];

        public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) { }
        public void DrawArc(HudPoint center, float radiusX, float radiusY, float startAngle, float sweepAngle, HudPaint paint, float thickness) { }
        public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness) { }
        public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint) { }
        public void DrawRect(HudRect rect, HudPaint paint, float thickness) { }
        public void FillRect(HudRect rect, HudPaint paint) { }
        public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint) { }
        public void DrawText(string text, HudPoint position, HudTextStyle style, HudPaint paint, HudTextAnchor anchor = HudTextAnchor.Center, HudTextBaseline baseline = HudTextBaseline.Middle) =>
            TextPositions.Add(new DrawnText(text, position));
        public HudSize MeasureText(string text, HudTextStyle style) => new(100, 20);
    }

    private sealed class TransformTrackingRenderContext : ITransformableRenderContext
    {
        public float Width => 1920;
        public float Height => 1080;

        public List<HudTransform> Transforms { get; } = [];

        public IDisposable PushTransform(HudTransform transform)
        {
            Transforms.Add(transform);
            return new NoOpDisposable();
        }

        public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) { }
        public void DrawArc(HudPoint center, float radiusX, float radiusY, float startAngle, float sweepAngle, HudPaint paint, float thickness) { }
        public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness) { }
        public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint) { }
        public void DrawRect(HudRect rect, HudPaint paint, float thickness) { }
        public void FillRect(HudRect rect, HudPaint paint) { }
        public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint) { }
        public void DrawText(string text, HudPoint position, HudTextStyle style, HudPaint paint, HudTextAnchor anchor = HudTextAnchor.Center, HudTextBaseline baseline = HudTextBaseline.Middle) { }
        public HudSize MeasureText(string text, HudTextStyle style) => new(100, 20);

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
