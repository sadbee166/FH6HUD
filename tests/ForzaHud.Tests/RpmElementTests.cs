using ForzaHud.Configuration;
using ForzaHud.Hud;
using ForzaHud.Hud.Elements;
using ForzaHud.Rendering;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class RpmElementTests
{
    [Theory]
    [InlineData(2000f, 220, 237, 239, 255)]
    [InlineData(4000f, 95, 208, 255, 255)]
    [InlineData(4500f, 95, 208, 255, 255)]
    [InlineData(5750f, 255, 179, 39, 255)]
    [InlineData(7000f, 255, 77, 77, 255)]
    public void DrawUsesTheExpectedColorAcrossEveryRpmZone(float rpm, byte r, byte g, byte b, byte a)
    {
        var settings = new ArcMeterSettings
        {
            ArcLength = 90f,
            Width = 3f,
            YellowZoneOffsetPercent = 0f,
            RedZoneOffsetPercent = 0f,
        };
        var context = new RecordingRenderContext();
        var element = new RpmElement(settings);
        var state = new DerivedState
        {
            Rpm = rpm,
            RpmRange = new RpmRange(1000f, 8000f),
            Powerband = new PowerbandState(
                IsLearned: true,
                PeakPowerRpm: 4500f,
                PowerbandStartRpm: 3000f,
                PowerbandEndRpm: 5000f,
                ShiftRpm: 6500f,
                RpmDropRatio: 0.72f),
        };
        var frame = new HudFrame(
            State: state,
            Display: new HudDisplay { Rpm = rpm },
            Visual: new VisualSettings(),
            Theme: HudTheme.From(new ThemeSettings()),
            Origin: new HudPoint(500f, 500f),
            Scale: 1f,
            Opacity: 1f,
            Width: 1000f,
            Height: 1000f,
            GForceFullScale: 1.5f);

        element.Draw(context, in frame);

        var activeArc = Assert.Single(context.Arcs, arc => arc.Thickness > settings.Width);
        Assert.Equal(new HudColor(r, g, b, a), activeArc.Paint.Color);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawsTcsBelowTheRpmReadoutUsingItsStateColor(bool active)
    {
        var settings = new ArcMeterSettings { ArcLength = 90f, Width = 3f };
        var theme = HudTheme.From(new ThemeSettings());
        var context = new RecordingRenderContext();
        var frame = new HudFrame(
            State: new DerivedState
            {
                Rpm = 4000f,
                RpmRange = new RpmRange(1000f, 8000f),
                TractionControlActive = active,
            },
            Display: new HudDisplay { Rpm = 4000f },
            Visual: new VisualSettings(),
            Theme: theme,
            Origin: new HudPoint(500f, 500f),
            Scale: 1f,
            Opacity: 1f,
            Width: 1000f,
            Height: 1000f,
            GForceFullScale: 1.5f);

        new RpmElement(settings).Draw(context, in frame);

        var tcs = Assert.Single(context.Texts, text => text.Text == "TCS");
        Assert.Equal(active ? theme.Accent : theme.Dim, tcs.Paint.Color);
        var rpmLabel = Assert.Single(context.Texts, text => text.Text == "RPM");
        Assert.True(tcs.Position.Y > rpmLabel.Position.Y);
    }

    [Fact]
    public void DrawStartsAtZeroRpmWhenIdle()
    {
        var settings = new ArcMeterSettings
        {
            ArcLength = 90f,
            Width = 3f,
            StartValue = 0f,
        };
        var context = new RecordingRenderContext();
        var frame = new HudFrame(
            State: new DerivedState { RpmRange = new RpmRange(1000f, 8000f) },
            Display: new HudDisplay { Rpm = 1000f },
            Visual: new VisualSettings(),
            Theme: HudTheme.From(new ThemeSettings()),
            Origin: new HudPoint(500f, 500f),
            Scale: 1f,
            Opacity: 1f,
            Width: 1000f,
            Height: 1000f,
            GForceFullScale: 1.5f);

        new RpmElement(settings).Draw(context, in frame);

        var activeArc = Assert.Single(context.Arcs, arc => arc.Thickness > settings.Width);
        Assert.Equal(11.25f, activeArc.SweepAngle, precision: 3);
    }

    [Fact]
    public void DrawUsesConfiguredArcOpacityMultipliers()
    {
        var settings = new ArcMeterSettings
        {
            ArcLength = 90f,
            Width = 3f,
        };
        var visual = new VisualSettings();
        visual.Opacity.ArcTrack = 0.25f;
        visual.Opacity.ArcMarker = 0.35f;
        visual.Opacity.ArcProminentMarker = 0.45f;
        var context = new RecordingRenderContext();
        var frame = new HudFrame(
            State: new DerivedState
            {
                Rpm = 2000f,
                RpmRange = new RpmRange(1000f, 8000f),
                Powerband = new PowerbandState(
                    IsLearned: true,
                    PeakPowerRpm: 4500f,
                    PowerbandStartRpm: 3000f,
                    PowerbandEndRpm: 5000f,
                    ShiftRpm: 6500f,
                    RpmDropRatio: 0.72f),
            },
            Display: new HudDisplay { Rpm = 2000f },
            Visual: visual,
            Theme: HudTheme.From(new ThemeSettings()),
            Origin: new HudPoint(500f, 500f),
            Scale: 1f,
            Opacity: 1f,
            Width: 1000f,
            Height: 1000f,
            GForceFullScale: 1.5f);

        new RpmElement(settings).Draw(context, in frame);

        var track = Assert.Single(context.Arcs, arc => arc.Thickness == settings.Width);
        Assert.Equal(0.25f, track.Paint.Opacity);
        Assert.Contains(context.Lines, line => line.Paint.Opacity == 0.35f);
        Assert.Contains(context.Lines, line => line.Paint.Opacity == 0.45f);
    }

    [Fact]
    public void DrawUsesBandStartPeakPowerAndShiftAsSemanticMarkers()
    {
        var settings = new ArcMeterSettings
        {
            ArcLength = 90f,
            Width = 3f,
            YellowZoneOffsetPercent = 0f,
            RedZoneOffsetPercent = 0f,
        };
        var theme = HudTheme.From(new ThemeSettings());
        var context = new RecordingRenderContext();
        var frame = new HudFrame(
            State: new DerivedState
            {
                RpmRange = new RpmRange(1000f, 8000f),
                Powerband = new PowerbandState(
                    IsLearned: true,
                    PeakPowerRpm: 4500f,
                    PowerbandStartRpm: 3000f,
                    PowerbandEndRpm: 5000f,
                    ShiftRpm: 6500f,
                    RpmDropRatio: 0.72f),
            },
            Display: new HudDisplay(),
            Visual: new VisualSettings(),
            Theme: theme,
            Origin: new HudPoint(500f, 500f),
            Scale: 1f,
            Opacity: 1f,
            Width: 1000f,
            Height: 1000f,
            GForceFullScale: 1.5f);

        new RpmElement(settings).Draw(context, in frame);

        var blue = Assert.Single(context.Lines, line => line.Paint.Color == theme.Accent);
        var yellow = Assert.Single(context.Lines, line => line.Paint.Color == theme.Warning);
        var red = Assert.Single(context.Lines, line => line.Paint.Color == theme.Critical);

        Assert.Equal(0.375f, MarkerProgress(blue, frame.Origin, settings.ArcLength), precision: 3);
        Assert.InRange(MarkerProgress(yellow, frame.Origin, settings.ArcLength), 0.562f, 0.563f);
        Assert.Equal(0.8125f, MarkerProgress(red, frame.Origin, settings.ArcLength), precision: 3);
        Assert.True(red.Paint.Opacity > yellow.Paint.Opacity);
    }

    [Fact]
    public void DrawKeepsEveryLoadedZoneAfterNonDrivingFrame()
    {
        var configuration = new HudConfiguration();
        configuration.Telemetry.Powerband.BinCount = 64;
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-rpm-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                new VehicleIdentity(2871, 0, 0, 6, 8500f),
                [
                    new PowerCurvePoint(2500f, 100f),
                    new PowerCurvePoint(3500f, 280f),
                    new PowerCurvePoint(4500f, 300f),
                    new PowerCurvePoint(5500f, 280f),
            ],
                new Dictionary<int, List<float>>() )));
            var processor = new VehicleStateProcessor(configuration, store);

            foreach (var sample in PowerSamples())
            {
                processor.Process(sample);
            }

            var state = processor.Process(TelemetrySnapshot.Empty);
            Assert.True(state.Powerband.IsLearned);
            Assert.True(state.RpmRange.IsValid);

            var settings = new ArcMeterSettings
            {
                ArcLength = 90f,
                Width = 3f,
                YellowZoneOffsetPercent = 0f,
                RedZoneOffsetPercent = 0f,
            };
            var theme = HudTheme.From(new ThemeSettings());
            var lower = state.RpmRange.IdleRpm;
            var upper = state.RpmRange.MaxRpm;
            var powerband = state.Powerband;
            var zoneCases = new[]
            {
                ((lower + powerband.PowerbandStartRpm) / 2f, theme.Primary),
                (powerband.PowerbandStartRpm, theme.Accent),
                ((powerband.PeakPowerRpm + powerband.ShiftRpm) / 2f, theme.Warning),
                ((powerband.ShiftRpm + upper) / 2f, theme.Critical),
            };

            foreach (var (rpm, expectedColor) in zoneCases)
            {
                var context = new RecordingRenderContext();
                var frame = new HudFrame(
                    State: state,
                    Display: new HudDisplay { Rpm = rpm },
                    Visual: new VisualSettings(),
                    Theme: theme,
                    Origin: new HudPoint(500f, 500f),
                    Scale: 1f,
                    Opacity: 1f,
                    Width: 1000f,
                    Height: 1000f,
                    GForceFullScale: 1.5f);

                new RpmElement(settings).Draw(context, in frame);

                var activeArc = Assert.Single(context.Arcs, arc => arc.Thickness > settings.Width);
                Assert.Equal(expectedColor, activeArc.Paint.Color);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static IEnumerable<TelemetrySnapshot> PowerSamples()
    {
        foreach (var (rpm, power) in new[]
        {
            (2500f, 100f),
            (3500f, 200f),
            (4500f, 300f),
            (5500f, 250f),
        })
        {
            for (var i = 0; i < 6; i++)
            {
                yield return TelemetrySnapshot.Empty with
                {
                    IsRaceOn = true,
                    EngineMaxRpm = 8500f,
                    EngineIdleRpm = 1300f,
                    CurrentEngineRpm = rpm,
                    Speed = 20f,
                    Power = power,
                    Throttle = 1f,
                    Gear = 3,
                    CarOrdinal = 2871,
                    NumCylinders = 6,
                };
            }
        }
    }

    private sealed class RecordingRenderContext : IRenderContext
    {
        public List<ArcCall> Arcs { get; } = [];

        public List<LineCall> Lines { get; } = [];

        public List<TextCall> Texts { get; } = [];

        public float Width => 1000f;

        public float Height => 1000f;

        public void DrawLine(HudPoint from, HudPoint to, HudPaint paint, float thickness) =>
            Lines.Add(new LineCall(from, to, paint, thickness));

        public void DrawArc(
            HudPoint center,
            float radiusX,
            float radiusY,
            float startAngle,
            float sweepAngle,
            HudPaint paint,
            float thickness) =>
            Arcs.Add(new ArcCall(startAngle, sweepAngle, paint, thickness));

        public void DrawEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint, float thickness)
        {
        }

        public void FillEllipse(HudPoint center, float radiusX, float radiusY, HudPaint paint)
        {
        }

        public void DrawRect(HudRect rect, HudPaint paint, float thickness)
        {
        }

        public void FillRect(HudRect rect, HudPaint paint)
        {
        }

        public void FillPolygon(ReadOnlySpan<HudPoint> points, HudPaint paint)
        {
        }

        public void DrawText(
            string text,
            HudPoint position,
            HudTextStyle style,
            HudPaint paint,
            HudTextAnchor anchor = HudTextAnchor.Center,
            HudTextBaseline baseline = HudTextBaseline.Middle)
        {
            Texts.Add(new TextCall(text, position, paint));
        }

        public HudSize MeasureText(string text, HudTextStyle style) => new(0f, 0f);
    }

    private readonly record struct ArcCall(
        float StartAngle,
        float SweepAngle,
        HudPaint Paint,
        float Thickness);

    private readonly record struct LineCall(
        HudPoint From,
        HudPoint To,
        HudPaint Paint,
        float Thickness);

    private readonly record struct TextCall(
        string Text,
        HudPoint Position,
        HudPaint Paint);

    private static float MarkerProgress(LineCall line, HudPoint origin, float arcLength)
    {
        var midpoint = new HudPoint(
            (line.From.X + line.To.X) / 2f,
            (line.From.Y + line.To.Y) / 2f);
        var angle = MathF.Atan2(midpoint.X - origin.X, origin.Y - midpoint.Y) * 180f / MathF.PI;
        if (angle < 0f)
        {
            angle += 360f;
        }

        var startAngle = 270f - arcLength / 2f;
        return (angle - startAngle) / arcLength;
    }
}
