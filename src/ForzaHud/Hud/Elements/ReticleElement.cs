using ForzaHud.Configuration;
using ForzaHud.Rendering;

namespace ForzaHud.Hud.Elements;

/// <summary>
/// The HUD reticle: a single thin ellipse that everything else is arranged around.
///
/// It carries no data. Its job is to give the instrument one coherent shape so the
/// individual elements read as a single cluster rather than loose widgets. The two halves
/// between the RPM and boost meters mirror RPM emphasis when it leaves the normal band.
/// </summary>
public sealed class ReticleElement : IHudElement
{
    /// <summary>Angular half-width of the gaps left at the top and bottom of the ring.</summary>
    private const float GapDegrees = 9f;

    private readonly ArcMeterSettings _rpmSettings;

    public ReticleElement(ArcMeterSettings rpmSettings) => _rpmSettings = rpmSettings;


    public string Id => DefaultElements.Ids.Reticle;

    public void Draw(IRenderContext context, in HudFrame frame)
    {
        var radius = frame.Visual.ReticleRadius * frame.Reference * frame.Scale;
        var color = RpmElement.CurrentZoneColor(in frame, _rpmSettings);
        if (color == frame.Theme.Primary)
        {
            // White is the normal RPM state; keep the reticle's existing dim appearance.
            color = frame.Theme.Dim;
        }

        var paint = new HudPaint(color, frame.Visual.Opacity.Reticle * frame.Opacity);
        var thickness = frame.Visual.LineThickness * frame.Visual.ReticleThicknessMultiplier;

        var gap = GapDegrees;
        var sweep = 180f - (2f * gap);

        context.DrawArc(frame.Origin, radius, radius, gap, sweep, paint, thickness);
        context.DrawArc(frame.Origin, radius, radius, 180f + gap, sweep, paint, thickness);

        var markerLength = radius * frame.Visual.ReticleLevelMarkerLength;
        if (markerLength > 0f)
        {
            var panelRotation = frame.Visual.PanelMotion.Enabled
                ? frame.Display.PanelMotionRollDegrees * frame.Visual.PanelMotion.RollMultiplier
                : 0f;
            var counterRotationDegrees = -panelRotation;
            var radians = MathF.PI * counterRotationDegrees / 180f;
            var cos = MathF.Cos(radians);
            var sin = MathF.Sin(radians);

            // 9 o'clock marker: starts at ring (-radius, 0) and extends inward to (-radius + markerLength, 0)
            var leftStart = new HudPoint(
                frame.Origin.X - radius * cos,
                frame.Origin.Y - radius * sin);
            var leftEnd = new HudPoint(
                frame.Origin.X - (radius - markerLength) * cos,
                frame.Origin.Y - (radius - markerLength) * sin);

            // 3 o'clock marker: starts at ring (+radius, 0) and extends inward to (+radius - markerLength, 0)
            var rightStart = new HudPoint(
                frame.Origin.X + radius * cos,
                frame.Origin.Y + radius * sin);
            var rightEnd = new HudPoint(
                frame.Origin.X + (radius - markerLength) * cos,
                frame.Origin.Y + (radius - markerLength) * sin);

            context.DrawLine(leftStart, leftEnd, paint, thickness);
            context.DrawLine(rightStart, rightEnd, paint, thickness);
        }
    }
}
