using System.Buffers.Binary;
using ForzaHud.Configuration;
using ForzaHud.Hud;
using ForzaHud.Rendering;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class AngularVelocityResponseTests
{
    [Fact]
    public void ParserDecodesPitchAndYawRatesAndLeavesRollRateOutOfSnapshot()
    {
        var buffer = new byte[ForzaPacketFormat.PacketSize];
        BinaryPrimitives.WriteSingleLittleEndian(
            buffer.AsSpan(ForzaPacketFormat.Sled.AngularVelocityX, 4), 1.25f);
        BinaryPrimitives.WriteSingleLittleEndian(
            buffer.AsSpan(ForzaPacketFormat.Sled.AngularVelocityY, 4), -2.5f);
        BinaryPrimitives.WriteSingleLittleEndian(
            buffer.AsSpan(ForzaPacketFormat.Sled.AngularVelocityZ, 4), 9f);

        Assert.True(ForzaPacketParser.TryParse(buffer, TimeSpan.Zero, out var snapshot));
        Assert.Equal(1.25f, snapshot.AngularVelocityX);
        Assert.Equal(-2.5f, snapshot.AngularVelocityY);
    }

    [Fact]
    public void VehicleStateProcessorDerivesAngularAccelerationFromPacketTime()
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());
        var first = TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            AngularVelocityX = 1f,
            AngularVelocityY = -2f,
            ReceivedAt = TimeSpan.FromSeconds(1),
        };
        var second = first with
        {
            AngularVelocityX = 1.5f,
            AngularVelocityY = -1f,
            ReceivedAt = TimeSpan.FromSeconds(1.25),
        };

        var firstState = processor.Process(first);
        var secondState = processor.Process(second);

        Assert.Equal(0f, firstState.PitchAngularAcceleration);
        Assert.Equal(0f, firstState.YawAngularAcceleration);
        Assert.Equal(2f, secondState.PitchAngularAcceleration, precision: 4);
        Assert.Equal(4f, secondState.YawAngularAcceleration, precision: 4);
    }

    [Fact]
    public void VehicleStateProcessorResetsAngularAccelerationAcrossNonDrivingFrame()
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());
        processor.Process(TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            AngularVelocityX = 1f,
            AngularVelocityY = 1f,
            ReceivedAt = TimeSpan.FromSeconds(1),
        });
        processor.Process(TelemetrySnapshot.Empty with
        {
            IsRaceOn = false,
            AngularVelocityX = 10f,
            AngularVelocityY = 10f,
            ReceivedAt = TimeSpan.FromSeconds(2),
        });

        var resumed = processor.Process(TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            AngularVelocityX = 20f,
            AngularVelocityY = 20f,
            ReceivedAt = TimeSpan.FromSeconds(3),
        });

        Assert.Equal(0f, resumed.PitchAngularAcceleration);
        Assert.Equal(0f, resumed.YawAngularAcceleration);
    }

    [Fact]
    public void HudEngineSmoothsAndResetsAngularAcceleration()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.PanelMotion.SmoothingMilliseconds = 100;
        var engine = new HudEngine(configuration);

        engine.Update(new DerivedState(), deltaSeconds: 0.016);
        engine.Update(
            new DerivedState
            {
                PitchAngularAcceleration = 10f,
                YawAngularAcceleration = -10f,
            },
            deltaSeconds: 0.05);

        Assert.InRange(engine.Display.PanelMotionPitchAngularAcceleration, 0f, 10f);
        Assert.InRange(engine.Display.PanelMotionYawAngularAcceleration, -10f, 0f);

        var reset = new DerivedState
        {
            PitchAngularAcceleration = -4f,
            YawAngularAcceleration = 3f,
        };
        engine.Reset(reset);
        engine.Update(reset, deltaSeconds: 0.05);

        Assert.Equal(-4f, engine.Display.PanelMotionPitchAngularAcceleration);
        Assert.Equal(3f, engine.Display.PanelMotionYawAngularAcceleration);
    }

    [Fact]
    public void PanelMotionCalculatorMapsAngularAccelerationToBoundedDepthAngles()
    {
        var settings = new PanelMotionSettings
        {
            Enabled = true,
            YawDegreesPerAngularAcceleration = 2f,
            PitchDegreesPerAngularAcceleration = 3f,
            MaximumYawRotationDegrees = 5f,
            MaximumPitchRotationDegrees = 6f,
            SpeedShake = new SpeedShakeSettings { Enabled = false },
        };
        var display = new HudDisplay
        {
            PanelMotionYawAngularAcceleration = 4f,
            PanelMotionPitchAngularAcceleration = -4f,
        };

        var motion = PanelMotionCalculator.Calculate(display, settings, reference: 1000f);

        Assert.Equal(5f, motion.DepthYawDegrees);
        Assert.Equal(-6f, motion.DepthPitchDegrees);
    }

    [Fact]
    public void DepthTransformKeepsItsRotationPivotFixed()
    {
        var pivot = new HudPoint(400f, 300f);
        var transform = new HudDepthTransform(20f, -15f, pivot, 1000f);

        var projected = transform.Project(pivot);

        Assert.Equal(pivot.X, projected.X, precision: 3);
        Assert.Equal(pivot.Y, projected.Y, precision: 3);
    }

    [Fact]
    public void RendererPushesDepthTransformAfterExistingPanelTransform()
    {
        var configuration = new HudConfiguration();
        configuration.Visual.PanelMotion.YawPivotX = 0.25f;
        configuration.Visual.PanelMotion.PitchPivotY = 0.75f;
        configuration.Visual.PanelMotion.SpeedShake.Enabled = false;

        var engine = new HudEngine(configuration);
        engine.Display.PanelMotionYawAngularAcceleration = 2f;
        engine.Display.PanelMotionPitchAngularAcceleration = -3f;

        var context = new DepthTrackingRenderContext();
        new HudRenderer(configuration).Draw(
            context,
            engine,
            new DerivedState { IsDriving = true },
            hasTelemetry: true);

        Assert.Equal(["affine", "depth"], context.Events.Take(2));
        var depth = Assert.Single(context.DepthTransforms);
        Assert.Equal(480f, depth.Pivot.X);
        Assert.Equal(810f, depth.Pivot.Y);
        Assert.Equal(2f, depth.YawDegrees);
        Assert.Equal(-3f, depth.PitchDegrees);
        Assert.Equal(1080f, depth.PerspectiveDistance);
    }

    private sealed class DepthTrackingRenderContext : ITransformableRenderContext, IDepthTransformableRenderContext
    {
        public float Width => 1920f;

        public float Height => 1080f;

        public List<string> Events { get; } = [];

        public List<HudDepthTransform> DepthTransforms { get; } = [];

        public IDisposable PushTransform(HudTransform transform)
        {
            Events.Add("affine");
            return NoOpDisposable.Instance;
        }

        public IDisposable PushDepthTransform(HudDepthTransform transform)
        {
            Events.Add("depth");
            DepthTransforms.Add(transform);
            return NoOpDisposable.Instance;
        }

        public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) { }
        public void DrawArc(HudPoint center, float radiusX, float radiusY, float startAngle, float sweepAngle, HudPaint paint, float thickness) { }
        public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness) { }
        public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint) { }
        public void DrawRect(HudRect rect, HudPaint paint, float thickness) { }
        public void FillRect(HudRect rect, HudPaint paint) { }
        public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint) { }
        public void DrawText(string text, HudPoint position, HudTextStyle style, HudPaint paint, HudTextAnchor anchor = HudTextAnchor.Center, HudTextBaseline baseline = HudTextBaseline.Middle) { }
        public HudSize MeasureText(string text, HudTextStyle style) => new(100f, 20f);

        private sealed class NoOpDisposable : IDisposable
        {
            public static NoOpDisposable Instance { get; } = new();

            public void Dispose() { }
        }
    }
}
