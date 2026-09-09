using ForzaHud.Common;
using ForzaHud.Configuration;
using ForzaHud.Vehicle;

namespace ForzaHud.Hud;

/// <summary>
/// Owns HUD behaviour: what each element should show, visual smoothing, and visibility.
///
/// It knows nothing about UDP offsets and performs no vehicle-physics calculations - those
/// belong to <see cref="VehicleStateProcessor"/> and the analysis classes beside it.
/// </summary>
public sealed class HudEngine
{
    private const float BoostMaximumFloor = 8f;

    private SmoothValue _rpm;
    private SmoothValue _rpmNormalized;
    private SmoothValue _speed;
    private SmoothValue _speedKph;
    private SmoothValue _throttle;
    private SmoothValue _brake;
    private SmoothValue _steer;
    private SmoothValue _boost;
    private SmoothValue _lateralG;
    private SmoothValue _longitudinalG;
    private SmoothValue _verticalG;
    private SmoothValue _panelMotionLateralG;
    private SmoothValue _panelMotionLongitudinalG;
    private SmoothValue _panelMotionVerticalG;
    private SmoothValue _panelMotionRollDegrees;
    private SmoothValue _panelMotionPitchAngularAcceleration;
    private SmoothValue _panelMotionYawAngularAcceleration;

    private readonly HudConfiguration _configuration;

    public HudEngine(HudConfiguration configuration)
    {
        _configuration = configuration;
        Display = new HudDisplay();
    }

    /// <summary>Current smoothed display values.</summary>
    public HudDisplay Display { get; }

    /// <summary>
    /// Advances the HUD state by <paramref name="deltaSeconds"/> using the newest derived
    /// telemetry. Called once per rendered frame, independently of how often telemetry arrives.
    /// </summary>
    public void Update(DerivedState state, double deltaSeconds)
    {
        var smoothing = _configuration.Visual.Smoothing;

        Display.Rpm = (float)_rpm.Update(state.Rpm, smoothing.RpmMilliseconds / 1000d, deltaSeconds);
        Display.RpmNormalized = (float)_rpmNormalized.Update(state.RpmNormalized, smoothing.RpmMilliseconds / 1000d, deltaSeconds);
        Display.Speed = (float)_speed.Update(state.DisplaySpeed, smoothing.SpeedMilliseconds / 1000d, deltaSeconds);
        Display.SpeedKph = (float)_speedKph.Update(
            SpeedConverter.Convert(state.SpeedMetersPerSecond, SpeedUnit.KilometersPerHour),
            smoothing.SpeedMilliseconds / 1000d,
            deltaSeconds);
        Display.AnimationTimeSeconds += Math.Max(0d, deltaSeconds);
        Display.Throttle = (float)_throttle.Update(state.Throttle, smoothing.PedalMilliseconds / 1000d, deltaSeconds);
        Display.Brake = (float)_brake.Update(state.Brake, smoothing.PedalMilliseconds / 1000d, deltaSeconds);
        Display.Steer = (float)_steer.Update(state.Steer, smoothing.SteerMilliseconds / 1000d, deltaSeconds);
        Display.Boost = (float)_boost.Update(state.BoostPsi, 0d, deltaSeconds);
        Display.LateralG = (float)_lateralG.Update(state.LateralG, smoothing.GForceMilliseconds / 1000d, deltaSeconds);
        Display.LongitudinalG = (float)_longitudinalG.Update(state.LongitudinalG, smoothing.GForceMilliseconds / 1000d, deltaSeconds);
        Display.VerticalG = (float)_verticalG.Update(state.VerticalG, smoothing.GForceMilliseconds / 1000d, deltaSeconds);

        var panelMotionSmoothing = _configuration.Visual.PanelMotion.SmoothingMilliseconds / 1000d;
        Display.PanelMotionLateralG = (float)_panelMotionLateralG.Update(
            state.LateralG,
            panelMotionSmoothing,
            deltaSeconds);
        Display.PanelMotionLongitudinalG = (float)_panelMotionLongitudinalG.Update(
            state.LongitudinalG,
            panelMotionSmoothing,
            deltaSeconds);
        Display.PanelMotionVerticalG = (float)_panelMotionVerticalG.Update(
            state.VerticalG,
            panelMotionSmoothing,
            deltaSeconds);
        Display.PanelMotionRollDegrees = (float)_panelMotionRollDegrees.Update(
            state.RollDegrees,
            panelMotionSmoothing,
            deltaSeconds);
        Display.PanelMotionPitchAngularAcceleration = (float)_panelMotionPitchAngularAcceleration.Update(
            state.PitchAngularAcceleration,
            panelMotionSmoothing,
            deltaSeconds);
        Display.PanelMotionYawAngularAcceleration = (float)_panelMotionYawAngularAcceleration.Update(
            state.YawAngularAcceleration,
            panelMotionSmoothing,
            deltaSeconds);

        // The boost arc has no meaningful fixed maximum, so it tracks what this car can do.
        if (state.BoostPsi > Display.BoostMaximum)
        {
            Display.BoostMaximum = state.BoostPsi;
        }

        Display.BoostMaximum = Math.Max(Display.BoostMaximum, BoostMaximumFloor);
    }

    /// <summary>
    /// Drops all smoothing history. Used when telemetry restarts so the HUD does not glide
    /// in from a stale value.
    /// </summary>
    public void Reset(DerivedState state)
    {
        _rpm.Reset(state.Rpm);
        _rpmNormalized.Reset(state.RpmNormalized);
        _speed.Reset(state.DisplaySpeed);
        _speedKph.Reset(SpeedConverter.Convert(state.SpeedMetersPerSecond, SpeedUnit.KilometersPerHour));
        _throttle.Reset(state.Throttle);
        _brake.Reset(state.Brake);
        _steer.Reset(state.Steer);
        _boost.Reset(state.BoostPsi);
        _lateralG.Reset(state.LateralG);
        _longitudinalG.Reset(state.LongitudinalG);
        _verticalG.Reset(state.VerticalG);
        _panelMotionLateralG.Reset(state.LateralG);
        _panelMotionLongitudinalG.Reset(state.LongitudinalG);
        _panelMotionVerticalG.Reset(state.VerticalG);
        _panelMotionRollDegrees.Reset(state.RollDegrees);
        _panelMotionPitchAngularAcceleration.Reset(state.PitchAngularAcceleration);
        _panelMotionYawAngularAcceleration.Reset(state.YawAngularAcceleration);
        Display.AnimationTimeSeconds = 0d;
    }
}
