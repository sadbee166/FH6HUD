using ForzaHud.Configuration;
using ForzaHud.Rendering;
using ForzaHud.Vehicle;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// Engine speed shown as a continuously filled left-side arc meter.
///
/// A circular tachometer would take more visual attention than the HUD is allowed, so RPM
/// is presented as a peripheral arc whose colour communicates where the engine is in its
/// useful range: primary below the main power band, accent up to peak power, warning through
/// the shift band, and critical from the shift point onward.
///
/// The powerband and shift point come from <see cref="PowerbandState"/>. Before anything has
/// been learned the arc falls back to fixed fractions of the rev range and never claims to
/// know where the powerband is.
/// </summary>
public sealed class RpmElement : IHudElement
{
    // Fallback zones, used only until a powerband has been learned.
    private const float FallbackPowerbandStart = 0.55f;
    private const float FallbackPeakPower = 0.88f;
    private const float FallbackShiftPoint = 0.95f;

    private readonly ArcMeterSettings _settings;

    public RpmElement(ArcMeterSettings settings) => _settings = settings;

    public string Id => DefaultElements.Ids.Rpm;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        var radius = ArcMeter.Radius(frame, layer: 0);
        var angles = ArcMeter.LeftAngles(_settings.ArcLength);
        var progress = Progress(in frame, _settings);

        var (powerbandStart, peakPower, shiftPoint) = ZoneThresholds(in frame, _settings);
        var semanticColor = ZoneColor(progress, powerbandStart, peakPower, shiftPoint, frame.Theme);
        var activeColor = ArcMeter.ResolveColor(_settings, semanticColor);

        ArcMeter.DrawProgress(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            frame.Display.Rpm,
            StartValue(in frame, _settings),
            frame.State.RpmRange.MaxRpm,
            _settings,
            activeColor);

        ArcMeter.DrawEndpointMarkers(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            _settings,
            ArcMeter.ResolveColor(_settings, frame.Theme.Primary));

        // Blue: entry into the top configured percentage of peak power.
        // Yellow: the engine's learned peak-power RPM.
        ArcMeter.DrawMarker(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            powerbandStart,
            frame.Theme.Accent,
            prominent: false,
            thickness: _settings.Width);
        ArcMeter.DrawMarker(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            peakPower,
            frame.Theme.Warning,
            prominent: false,
            thickness: _settings.Width);

        // Shift point: brighter and longer, because it is the one that demands a response.
        ArcMeter.DrawMarker(
            context,
            in frame,
            radius,
            angles.StartAngle,
            angles.SweepAngle,
            shiftPoint,
            frame.Theme.Critical,
            prominent: true,
            thickness: _settings.Width);

        DrawReadout(context, in frame, radius);
    }

    /// <summary>Returns the current RPM emphasis colour using the same value as the meter.</summary>
    internal static HudColor CurrentZoneColor(in HudFrame frame, ArcMeterSettings settings)
    {
        var (powerbandStart, peakPower, shiftPoint) = ZoneThresholds(in frame, settings);
        var progress = Progress(in frame, settings);
        return ArcMeter.ResolveColor(
            settings,
            ZoneColor(progress, powerbandStart, peakPower, shiftPoint, frame.Theme));
    }

    private static (float PowerbandStart, float PeakPower, float ShiftPoint) ZoneThresholds(
        in HudFrame frame,
        ArcMeterSettings settings)
    {
        var powerband = frame.State.Powerband;
        var range = frame.State.RpmRange;

        if (powerband.IsLearned && range.IsValid)
        {
            var startValue = StartValue(in frame, settings);
            if (startValue >= range.MaxRpm)
            {
                return (1f, 1f, 1f);
            }

            return (
                ArcMeter.NormalizeValue(powerband.PowerbandStartRpm, startValue, range.MaxRpm),
                ArcMeter.NormalizeValue(OffsetLower(powerband.PeakPowerRpm, settings.YellowZoneOffsetPercent), startValue, range.MaxRpm),
                ArcMeter.NormalizeValue(OffsetLower(powerband.ShiftRpm, settings.RedZoneOffsetPercent), startValue, range.MaxRpm));
        }

        if (!range.IsValid)
        {
            return (FallbackPowerbandStart, FallbackPeakPower, FallbackShiftPoint);
        }

        var fallbackStart = StartValue(in frame, settings);
        var fallbackRange = range.MaxRpm - fallbackStart;
        if (fallbackRange <= 0f)
        {
            return (0f, 0f, 0f);
        }

        return (
            ArcMeter.NormalizeValue(fallbackStart + fallbackRange * FallbackPowerbandStart, fallbackStart, range.MaxRpm),
            ArcMeter.NormalizeValue(
                OffsetLower(fallbackStart + fallbackRange * FallbackPeakPower, settings.YellowZoneOffsetPercent),
                fallbackStart,
                range.MaxRpm),
            ArcMeter.NormalizeValue(
                OffsetLower(fallbackStart + fallbackRange * FallbackShiftPoint, settings.RedZoneOffsetPercent),
                fallbackStart,
                range.MaxRpm));
    }

    private static float Progress(in HudFrame frame, ArcMeterSettings settings)
    {
        var range = frame.State.RpmRange;
        return range.IsValid
            ? ArcMeter.NormalizeValue(frame.Display.Rpm, StartValue(in frame, settings), range.MaxRpm)
            : 0f;
    }

    private static float StartValue(in HudFrame frame, ArcMeterSettings settings) =>
        MathF.Max(settings.StartValue, 0f);

    private static float OffsetLower(float value, float offsetPercent) =>
        value * Math.Clamp(1f - offsetPercent / 100f, 0f, 1f);

    private static void DrawReadout(IRenderContext context, in HudFrame frame, float radius)
    {
        var rpm = (int)Math.Clamp(frame.Display.Rpm, 0f, 99999f);
        var text = rpm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var readoutPoint = ArcMeter.PointOnCircle(
            frame.Origin,
            radius + 17f * frame.Scale,
            270f);

        context.DrawText(
            text,
            new HudPoint(readoutPoint.X, readoutPoint.Y - 11f * frame.Scale),
            HudTextStyle.Value,
            new HudPaint(frame.Theme.Primary, frame.Opacity),
            HudTextAnchor.Trailing);

        context.DrawText(
            "RPM",
            new HudPoint(readoutPoint.X, readoutPoint.Y + 15f * frame.Scale),
            HudTextStyle.Label,
            new HudPaint(frame.Theme.Dim, frame.Opacity),
            HudTextAnchor.Trailing,
            HudTextBaseline.Top);

        context.DrawText(
            "TCS",
            new HudPoint(readoutPoint.X, readoutPoint.Y + 30f * frame.Scale),
            HudTextStyle.Label,
            new HudPaint(
                frame.State.TractionControlActive ? frame.Theme.Accent : frame.Theme.Dim,
                frame.Opacity),
            HudTextAnchor.Trailing,
            HudTextBaseline.Top);
    }

    private static HudColor ZoneColor(
        float normalized,
        float powerbandStart,
        float peakPower,
        float shiftPoint,
        HudTheme theme)
    {
        if (normalized >= shiftPoint)
        {
            return theme.Critical;
        }

        if (normalized > peakPower)
        {
            return theme.Warning;
        }

        if (normalized >= powerbandStart)
        {
            return theme.Accent;
        }

        return theme.Primary;
    }
}
