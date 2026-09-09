using ForzaHud.Configuration;
using ForzaHud.Hud.Elements;
using ForzaHud.Rendering;
using ForzaHud.Vehicle;

namespace ForzaHud.Hud;

/// <summary>
/// Drives the HUD elements against a render context.
///
/// The renderer does no deciding: each element receives the derived state, the smoothed
/// display values, its placement and the theme, and draws itself.
/// </summary>
public sealed class HudRenderer
{
    private readonly HudConfiguration _configuration;
    private readonly HudTheme _theme;
    private readonly IHudElement[] _elements;
    private readonly CalibrationIndicatorElement _calibrationIndicator;

    public HudRenderer(HudConfiguration configuration)
    {
        _configuration = configuration;
        _theme = HudTheme.From(configuration.Visual.Theme);
        _calibrationIndicator = new CalibrationIndicatorElement();

        _elements =
        [
            new ReticleElement(configuration.Visual.Meters.Rpm),
            new SteerElement(configuration.Visual.Meters.Steer),
            new RpmElement(configuration.Visual.Meters.Rpm),
            new BoostElement(configuration.Visual.Meters.Boost),
            new PedalsElement(configuration.Visual.Meters.Brake, configuration.Visual.Meters.Throttle),
            new GearElement(configuration.Element(DefaultElements.Ids.Gear)),
            new SpeedElement(configuration.Element(DefaultElements.Ids.Speed)),
            new GForceElement(configuration.Element(DefaultElements.Ids.GForce)),
            new TireGripElement(configuration.Element(DefaultElements.Ids.Tires)),
        ];
    }

    /// <summary>
    /// Draws one frame.
    /// </summary>
    /// <param name="context">Drawing API to render through.</param>
    /// <param name="engine">HUD engine holding the current display values.</param>
    /// <param name="state">Latest derived vehicle state.</param>
    /// <param name="hasTelemetry">False when no telemetry has arrived; the HUD is dimmed unless configured to hide.</param>
    /// <param name="isCalibrationRecording">Whether to show the RPM calibration status indicator.</param>
    public void Draw(
        IRenderContext context,
        HudEngine engine,
        DerivedState state,
        bool hasTelemetry,
        bool isCalibrationRecording = false)
    {
        if (isCalibrationRecording)
        {
            DrawCalibrationIndicator(context, engine, state);
        }

        var tractionControl = _configuration.Telemetry.TractionControl;
        if (tractionControl.Enabled
            && tractionControl.DetectionMode == TcsDetectionMode.Frame
            && tractionControl.Frame.ShowDetectionZone)
        {
            FrameTcsDebugOverlay.Draw(
                context,
                tractionControl.Frame,
                _theme,
                _configuration.Visual,
                (float)_configuration.Overlay.Opacity);
        }

        if (_configuration.Overlay.HideWhenNotDriving && (!hasTelemetry || !state.IsDriving))
        {
            return;
        }

        var panelMotion = PanelMotionCalculator.Calculate(
            engine.Display,
            _configuration.Visual.PanelMotion,
            Math.Min(context.Width, context.Height));
        var panelAnchor = new HudPoint(context.Width / 2f, context.Height / 2f);

        var transformContext = context as ITransformableRenderContext;
        var depthTransformContext = context as IDepthTransformableRenderContext;
        using (transformContext?.PushTransform(
            new HudTransform(panelMotion.Scale, panelAnchor, panelMotion.Offset, panelMotion.RotationDegrees)))
        using (depthTransformContext?.PushDepthTransform(
            new HudDepthTransform(
                panelMotion.DepthYawDegrees,
                panelMotion.DepthPitchDegrees,
                new HudPoint(
                    _configuration.Visual.PanelMotion.YawPivotX * context.Width,
                    _configuration.Visual.PanelMotion.PitchPivotY * context.Height),
                Math.Min(context.Width, context.Height))))
        {
            DrawElements(
                context,
                engine,
                state,
                hasTelemetry,
                panelMotion,
                panelAnchor,
                transformContext is not null);
        }

        if (!hasTelemetry)
        {
            context.DrawText(
                "NO TELEMETRY",
                new HudPoint(context.Width / 2f, context.Height * 0.5f),
                HudTextStyle.Label,
                new HudPaint(
                    _theme.Dim,
                    _configuration.Visual.Opacity.NoTelemetryLabel
                    * (float)_configuration.Overlay.Opacity));
        }
    }

    private void DrawCalibrationIndicator(
        IRenderContext context,
        HudEngine engine,
        DerivedState state)
    {
        var settings = _configuration.Element(DefaultElements.Ids.Calibration);
        if (!settings.Enabled)
        {
            return;
        }

        var frame = new HudFrame(
            State: state,
            Display: engine.Display,
            Visual: _configuration.Visual,
            Theme: _theme,
            Origin: new HudPoint(
                (float)(settings.X * context.Width),
                (float)(settings.Y * context.Height)),
            Scale: (float)settings.Scale,
            Opacity: (float)(_configuration.Overlay.Opacity * settings.Opacity),
            Width: context.Width,
            Height: context.Height,
            GForceFullScale: _configuration.Telemetry.GForce.FullScaleG);

        _calibrationIndicator.Draw(context, in frame);
    }

    private void DrawElements(
        IRenderContext context,
        HudEngine engine,
        DerivedState state,
        bool hasTelemetry,
        PanelMotion panelMotion,
        HudPoint panelAnchor,
        bool hasRenderTransform)
    {
        var dim = hasTelemetry ? 1f : _configuration.Visual.Opacity.NoTelemetryElements;
        var transformContext = context as ITransformableRenderContext;
        var levelSpeedAndGear = _configuration.Visual.PanelMotion.LevelSpeedAndGear
            && panelMotion.RotationDegrees != 0f;

        foreach (var element in _elements)
        {
            var settings = _configuration.Element(element.Id);
            if (!settings.Enabled)
            {
                continue;
            }

            var isLevelReadout = levelSpeedAndGear
                && (element.Id == DefaultElements.Ids.Gear || element.Id == DefaultElements.Ids.Speed);

            var configuredOrigin = new HudPoint(
                (float)(settings.X * context.Width),
                (float)(settings.Y * context.Height));

            HudPoint origin;
            if (hasRenderTransform)
            {
                origin = configuredOrigin;
            }
            else if (isLevelReadout)
            {
                origin = new HudPoint(
                    panelAnchor.X + (configuredOrigin.X - panelAnchor.X) * panelMotion.Scale + panelMotion.Offset.X,
                    panelAnchor.Y + (configuredOrigin.Y - panelAnchor.Y) * panelMotion.Scale + panelMotion.Offset.Y);
            }
            else
            {
                origin = panelMotion.Apply(configuredOrigin, panelAnchor);
            }

            var frame = new HudFrame(
                State: state,
                Display: engine.Display,
                Visual: _configuration.Visual,
                Theme: _theme,
                Origin: origin,
                Scale: (float)settings.Scale * (hasRenderTransform ? 1f : panelMotion.Scale),
                Opacity: (float)(_configuration.Overlay.Opacity * settings.Opacity) * dim,
                Width: context.Width,
                Height: context.Height,
                GForceFullScale: _configuration.Telemetry.GForce.FullScaleG);

            if (hasRenderTransform && isLevelReadout)
            {
                using (transformContext?.PushTransform(
                    new HudTransform(1f, panelAnchor, new HudPoint(0f, 0f), -panelMotion.RotationDegrees)))
                {
                    element.Draw(context, in frame);
                }
            }
            else
            {
                element.Draw(context, in frame);
            }
        }
    }
}
