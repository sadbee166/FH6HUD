using ForzaHud.Rendering;

namespace ForzaHud.Configuration;

/// <summary>
/// Root of the HUD configuration file. Every section carries a usable default so the
/// application can start with no user-created configuration at all.
/// </summary>
public sealed class HudConfiguration
{
    public UdpSettings Udp { get; set; } = new();

    public UnitsSettings Units { get; set; } = new();

    public OverlaySettings Overlay { get; set; } = new();

    public VisualSettings Visual { get; set; } = new();

    public TelemetrySettings Telemetry { get; set; } = new();

    public RecordingSettings Recording { get; set; } = new();

    public CalibrationSettings Calibration { get; set; } = new();

    /// <summary>
    /// Per-element overrides keyed by element id: steer, rpm, boost, pedals, gear, speed,
    /// gforce, tires, reticle. Positions are normalized: (0,0) is the top-left of the
    /// overlay, (1,1) the bottom-right.
    /// </summary>
    public Dictionary<string, ElementSettings> Elements { get; set; } = DefaultElements.Create();

    /// <summary>
    /// Returns the settings for an element, falling back to the built-in default when the
    /// configuration file does not mention it.
    /// </summary>
    public ElementSettings Element(string id) =>
        Elements.TryGetValue(id, out var settings) ? settings : DefaultElements.Create()[id];
}

public sealed class UdpSettings
{
    /// <summary>Local address to bind. "127.0.0.1" for the same PC, "0.0.0.0" for any source.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Must match Data Out IP Port in FH6. The game binds its own socket to 5200-5300; avoid that range.</summary>
    public int Port { get; set; } = 2247;
}

public enum SpeedUnit
{
    KilometersPerHour,
    MilesPerHour,
}

public sealed class UnitsSettings
{
    public SpeedUnit Speed { get; set; } = SpeedUnit.KilometersPerHour;
}

public sealed class OverlaySettings
{
    /// <summary>Monitor index. 0 is the primary display.</summary>
    public int Monitor { get; set; }

    /// <summary>Overall HUD opacity, 0..1.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Whether to hide the HUD when FH6 reports that the player is not driving.</summary>
    public bool HideWhenNotDriving { get; set; }

    /// <summary>Render loop cap. The game emits at its frame rate; higher adds smoothness, not data.</summary>
    public int TargetFramesPerSecond { get; set; } = 120;
}

public sealed class VisualSettings
{
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Stroke width in device-independent pixels for HUD geometry.</summary>
    public float LineThickness { get; set; } = 1.6f;

    public TypographySettings Typography { get; set; } = new();

    /// <summary>Reticle radius as a fraction of the smaller overlay dimension.</summary>
    public float ReticleRadius { get; set; } = 0.30f;

    /// <summary>Length of the reticle level markers at 9 and 3 o'clock as a fraction of reticle radius.</summary>
    public float ReticleLevelMarkerLength { get; set; } = 0.10f;

    /// <summary>Extra spacing between related elements, in device-independent pixels.</summary>
    public float Spacing { get; set; } = 10f;

    /// <summary>G-force-driven translation and scale of the complete HUD panel.</summary>
    public PanelMotionSettings PanelMotion { get; set; } = new();

    /// <summary>Per-meter geometry and value mapping for the side-mounted arc meters.</summary>
    public ArcMetersSettings Meters { get; set; } = new();

    /// <summary>Opacity multipliers for secondary and supporting visual details.</summary>
    public VisualOpacitySettings Opacity { get; set; } = new();

    public ThemeSettings Theme { get; set; } = new();

    public SmoothingSettings Smoothing { get; set; } = new();
}

/// <summary>Opacity multipliers for visual details, applied in addition to overlay and element opacity.</summary>
public sealed class VisualOpacitySettings
{
    public float ArcTrack { get; set; } = 0.55f;

    public float ArcMarker { get; set; } = 0.70f;

    public float ArcProminentMarker { get; set; } = 0.95f;

    public float Reticle { get; set; } = 0.55f;

    public float GForceOuterRing { get; set; } = 0.45f;

    public float GForceReferenceRing { get; set; } = 0.70f;

    public float GForceCrosshair { get; set; } = 0.55f;

    public float GForceTrace { get; set; } = 0.50f;

    public float TireCenterLine { get; set; } = 0.28f;

    public float TireLostGripFill { get; set; } = 0.85f;

    public float TireWithinGripOutline { get; set; } = 0.60f;

    public float NoTelemetryElements { get; set; } = 0.35f;

    public float NoTelemetryLabel { get; set; } = 0.90f;
}

/// <summary>Configuration for the HUD's arc meters.</summary>
public sealed class ArcMetersSettings
{
    public ArcMeterSettings Steer { get; set; } = new();

    public ArcMeterSettings Rpm { get; set; } = new();

    public ArcMeterSettings Boost { get; set; } = new();

    public ArcMeterSettings Brake { get; set; } = new();

    public ArcMeterSettings Throttle { get; set; } = new();
}

/// <summary>
/// Visual and value-range settings for one arc meter.
/// </summary>
public sealed class ArcMeterSettings
{
    /// <summary>Angular length of the arc, in degrees.</summary>
    public float ArcLength { get; set; } = 90f;

    /// <summary>Base track stroke width in device-independent pixels.</summary>
    public float Width { get; set; } = 1.6f;

    /// <summary>
    /// Optional active-arc colour override. Null preserves the meter's
    /// semantic theme colours.
    /// </summary>
    public HudColor? Color { get; set; }

    /// <summary>
    /// Lowest value represented at the beginning of this meter, in its native unit:
    /// RPM for RPM, PSI for boost, and 0..1 for brake/throttle. Steering uses signed input
    /// around neutral instead of this range.
    /// </summary>
    public float StartValue { get; set; }

    /// <summary>
    /// Percentage by which the calculated peak-power RPM marker is shown lower on the meter.
    /// Used only by the RPM meter; 5 means 5 percent lower.
    /// </summary>
    public float YellowZoneOffsetPercent { get; set; }

    /// <summary>
    /// Percentage by which the calculated RPM red-zone threshold is shown lower on the
    /// meter. Used only by the RPM meter; 5 means 5 percent lower.
    /// </summary>
    public float RedZoneOffsetPercent { get; set; }

    /// <summary>Horizontal offset for this meter's label and value, in device-independent pixels.</summary>
    public float LabelOffsetX { get; set; }

    /// <summary>Vertical offset for this meter's label and value, in device-independent pixels.</summary>
    public float LabelOffsetY { get; set; }
}

public sealed class TypographySettings
{
    /// <summary>Small supporting labels.</summary>
    public float LabelSize { get; set; } = 11f;

    /// <summary>Secondary readouts such as boost.</summary>
    public float ValueSize { get; set; } = 26f;

    /// <summary>Gear indicator.</summary>
    public float GearSize { get; set; } = 42f;

    /// <summary>Dominant readout: speed.</summary>
    public float PrimarySize { get; set; } = 58f;
}

/// <summary>
/// G-force response for the complete HUD panel.
///
/// Positive longitudinal G is acceleration: the panel contracts and moves down. Negative
/// longitudinal G is deceleration: the panel expands and moves up. Positive lateral G is
/// force to the right, so the panel moves right. Translation values are fractions of the
/// shorter overlay dimension per G. Sensitivities are signed: a negative value reverses
/// that response.
/// </summary>
public sealed class PanelMotionSettings
{
    /// <summary>Whether G-force-driven panel motion is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Panel motion smoothing time constant, in milliseconds. 0 means no smoothing.</summary>
    public double SmoothingMilliseconds { get; set; } = 120;

    /// <summary>Signed panel scale change per longitudinal G. Positive values shrink under acceleration.</summary>
    public float ScalePerLongitudinalG { get; set; } = 0.08f;

    /// <summary>Signed panel scale change per 100 KPH of road speed. Positive values shrink as speed increases.</summary>
    public float ScalePer100Kph { get; set; } = 0.04f;

    /// <summary>Signed vertical panel movement per longitudinal G, as a fraction of the shorter overlay dimension.</summary>
    public float VerticalMovePerLongitudinalG { get; set; } = 0.025f;

    /// <summary>Signed vertical panel movement per vertical G, as a fraction of the shorter overlay dimension.</summary>
    public float VerticalMovePerVerticalG { get; set; } = 0.025f;

    /// <summary>Signed horizontal panel movement per lateral G, as a fraction of the shorter overlay dimension.</summary>
    public float HorizontalMovePerLateralG { get; set; } = 0.025f;

    /// <summary>Signed roll multiplier for complete-panel rotation. -1 counter-rotates (world-level horizon).</summary>
    public float RollMultiplier { get; set; } = -1f;

    /// <summary>Normalized screen-space X coordinate of the yaw rotation pivot.</summary>
    public float YawPivotX { get; set; } = 0.5f;

    /// <summary>Normalized screen-space Y coordinate of the pitch rotation pivot.</summary>
    public float PitchPivotY { get; set; } = 0.5f;

    /// <summary>Yaw rotation degrees produced by one radian per second squared of yaw acceleration.</summary>
    public float YawDegreesPerAngularAcceleration { get; set; } = 1f;

    /// <summary>Pitch rotation degrees produced by one radian per second squared of pitch acceleration.</summary>
    public float PitchDegreesPerAngularAcceleration { get; set; } = 1f;

    /// <summary>Largest absolute yaw depth-rotation angle in degrees.</summary>
    public float MaximumYawRotationDegrees { get; set; } = 12f;

    /// <summary>Largest absolute pitch depth-rotation angle in degrees.</summary>
    public float MaximumPitchRotationDegrees { get; set; } = 12f;

    /// <summary>Whether gear and speed text counter-rotate to stay level along with the reticle level markers.</summary>
    public bool LevelSpeedAndGear { get; set; } = true;

    /// <summary>Speed-dependent shake applied to the complete panel.</summary>
    public SpeedShakeSettings SpeedShake { get; set; } = new();

    /// <summary>Smallest panel scale allowed after longitudinal and speed response.</summary>
    public float MinimumScale { get; set; }

    /// <summary>Largest panel scale allowed after longitudinal and speed response.</summary>
    public float MaximumScale { get; set; } = 1.25f;
}

/// <summary>Configuration for the speed-dependent panel shake.</summary>
public sealed class SpeedShakeSettings
{
    /// <summary>Whether speed-dependent panel shake is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Speed at which shake begins, in KPH.</summary>
    public float StartSpeedKph { get; set; } = 72f;

    /// <summary>Speed at which shake reaches full strength, in KPH.</summary>
    public float FullStrengthSpeedKph { get; set; } = 216f;

    /// <summary>Maximum shake travel as a fraction of the shorter overlay dimension.</summary>
    public float Amplitude { get; set; } = 0.006f;

    /// <summary>Shake frequency in cycles per second.</summary>
    public float FrequencyHz { get; set; } = 16f;
}

public sealed class ThemeSettings
{
    /// <summary>Primary text and geometry.</summary>
    public HudColor Primary { get; set; } = new(220, 237, 239, 255);

    /// <summary>Inactive or de-emphasised geometry.</summary>
    public HudColor Dim { get; set; } = new(78, 97, 105, 255);

    /// <summary>Values worth noticing but not alarming.</summary>
    public HudColor Accent { get; set; } = new(95, 208, 255, 255);

    /// <summary>Approaching a limit.</summary>
    public HudColor Warning { get; set; } = new(255, 179, 39, 255);

    /// <summary>At or beyond a limit.</summary>
    public HudColor Critical { get; set; } = new(255, 77, 77, 255);
}

/// <summary>
/// Smoothing time constants in milliseconds. 0 means no smoothing at all.
///
/// Driver inputs are deliberately zero: the HUD must feel immediate. Only graphical
/// movement and the G cursor are smoothed.
/// </summary>
public sealed class SmoothingSettings
{
    public double RpmMilliseconds { get; set; } = 45;

    public double PedalMilliseconds { get; set; }

    public double SteerMilliseconds { get; set; }

    public double GForceMilliseconds { get; set; } = 60;

    public double SpeedMilliseconds { get; set; }
}

public sealed class TelemetrySettings
{
    public GForceSettings GForce { get; set; } = new();

    public GripSettings Grip { get; set; } = new();

    public PowerbandSettings Powerband { get; set; } = new();

    public TractionControlSettings TractionControl { get; set; } = new();
}

/// <summary>Thresholds and hysteresis for inferred traction-control intervention.</summary>
public sealed class TractionControlSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Source used to determine whether the game's traction-control indicator is on.</summary>
    public TcsDetectionMode DetectionMode { get; set; } = TcsDetectionMode.Telemetry;

    /// <summary>Screen-region settings used when <see cref="DetectionMode"/> is Frame.</summary>
    public FrameTcsSettings Frame { get; set; } = new();

    /// <summary>Whether engine-power suppression contributes TCS evidence.</summary>
    public bool EnginePowerEvidenceEnabled { get; set; } = true;

    /// <summary>Throttle must be at least this high on two adjacent samples.</summary>
    public float MinimumThrottle { get; set; } = 0.90f;

    /// <summary>Allowed throttle decrease while treating the driver demand as stable.</summary>
    public float MaximumThrottleDecrease { get; set; } = 0.04f;

    /// <summary>Minimum absolute driven-wheel slip for slip-correlated evidence.</summary>
    public float MinimumDrivenSlip { get; set; } = 0.20f;

    /// <summary>Minimum driven-wheel slip increase between adjacent samples.</summary>
    public float MinimumDrivenSlipIncrease { get; set; } = 0.04f;

    /// <summary>Minimum RPM increase for fallback slip-correlated evidence.</summary>
    public float MinimumRpmIncrease { get; set; } = 25f;

    /// <summary>Minimum RPM drop that marks a same-gear transmission transient.</summary>
    public float MinimumShiftRpmDrop { get; set; } = 250f;

    /// <summary>RPM fraction above which limiter behavior is ignored.</summary>
    public float LimiterRpmFraction { get; set; } = 0.985f;

    /// <summary>Power drop below the local trend that counts as an abrupt cut.</summary>
    public float TransientPowerDropFraction { get; set; } = 0.25f;

    /// <summary>Maximum decline already present immediately before an abrupt cut.</summary>
    public float MaximumPreCutDeclineFraction { get; set; } = 0.10f;

    /// <summary>Maximum power rise treated as a flattening fallback response.</summary>
    public float MaximumPowerRiseFraction { get; set; } = 0.02f;

    /// <summary>Number of preceding samples used to establish local power trend.</summary>
    public int TrendSampleCount { get; set; } = 4;

    /// <summary>Fallback history window used to recognize repeated power cuts.</summary>
    public int RepeatedCutWindowSamples { get; set; } = 8;

    /// <summary>Cut events required in the fallback history for repeated-cut evidence.</summary>
    public int MinimumRepeatedCuts { get; set; } = 2;

    /// <summary>Consecutive qualifying samples needed to activate TCS.</summary>
    public int ActivationSamples { get; set; } = 3;

    /// <summary>Consecutive non-evidence samples needed to deactivate TCS.</summary>
    public int DeactivationSamples { get; set; } = 4;

    /// <summary>Minimum qualifying evidence duration before activating TCS.</summary>
    public double AttackMilliseconds { get; set; } = 10;

    /// <summary>Minimum non-evidence duration before deactivating TCS.</summary>
    public double ReleaseMilliseconds { get; set; } = 30;

    /// <summary>Largest receive-time gap that may join two observations.</summary>
    public double MaximumTelemetryGapMilliseconds { get; set; } = 150;

    /// <summary>Number of samples ignored after a shift or transmission transient when receive time is unavailable.</summary>
    public int ShiftSuppressionSamples { get; set; } = 20;

    /// <summary>Time ignored after a shift or transmission transient when receive time is available.</summary>
    public double ShiftSuppressionMilliseconds { get; set; } = 400;

    public float MaximumBrake { get; set; } = 0.05f;

    public float MaximumClutch { get; set; } = 0.05f;

    public float MaximumHandBrake { get; set; } = 0.05f;

    public WheelSpeedTcsSettings WheelSpeed { get; set; } = new();

    public TireSlipTcsSettings TireSlip { get; set; } = new();
}

public enum TcsDetectionMode
{
    Telemetry,
    Frame,
}

/// <summary>Settings for reading the in-game TCR indicator from a captured screen region.</summary>
public sealed class FrameTcsSettings
{
    /// <summary>Whether to draw the configured detection region on the HUD for calibration.</summary>
    public bool ShowDetectionZone { get; set; }

    /// <summary>Left edge of the indicator region as a fraction of the selected monitor.</summary>
    public float RegionX { get; set; } = 0.950f;

    /// <summary>Top edge of the indicator region as a fraction of the selected monitor.</summary>
    public float RegionY { get; set; } = 0.925f;

    /// <summary>Width of the indicator region as a fraction of the selected monitor.</summary>
    public float RegionWidth { get; set; } = 0.025f;

    /// <summary>Height of the indicator region as a fraction of the selected monitor.</summary>
    public float RegionHeight { get; set; } = 0.020f;

    /// <summary>Minimum number of cyan pixels required to classify the indicator as on.</summary>
    public int MinimumOnPixels { get; set; } = 20;

    /// <summary>Minimum green and blue channel value for a cyan indicator pixel.</summary>
    public int MinimumCyanChannel { get; set; } = 120;

    /// <summary>Minimum amount by which green and blue must each exceed red.</summary>
    public int MinimumCyanDominance { get; set; } = 50;
}

/// <summary>Thresholds and hysteresis for sustained driven-tyre slip evidence.</summary>
public sealed class TireSlipTcsSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum driven-wheel slip used for FWD vehicles.</summary>
    public float FwdMinimumDrivenSlip { get; set; } = 0.02f;

    /// <summary>Minimum driven-wheel slip used for RWD vehicles.</summary>
    public float RwdMinimumDrivenSlip { get; set; } = 0.20f;

    /// <summary>Minimum driven-wheel slip used for AWD vehicles.</summary>
    public float AwdMinimumDrivenSlip { get; set; } = 0.32f;

    /// <summary>Consecutive slip samples needed to activate FWD detection.</summary>
    public int FwdActivationSamples { get; set; } = 16;

    /// <summary>Consecutive slip samples needed to activate RWD detection.</summary>
    public int RwdActivationSamples { get; set; } = 8;

    /// <summary>Consecutive slip samples needed to activate AWD detection.</summary>
    public int AwdActivationSamples { get; set; } = 12;

    /// <summary>Consecutive non-slip samples needed to deactivate detection.</summary>
    public int DeactivationSamples { get; set; } = 4;
}

/// <summary>Thresholds for wheel-speed-only TCS evidence.</summary>
public sealed class WheelSpeedTcsSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Exponential filter weight applied to each raw wheel speed.</summary>
    public float FilterAlpha { get; set; } = 0.70f;

    /// <summary>Wheel speed above which a front/rear ratio may be learned.</summary>
    public float MinimumLearningSpeed { get; set; } = 8f;

    /// <summary>Slow update weight for the front/rear rolling-circumference ratio.</summary>
    public float BaselineLearningRate { get; set; } = 0.02f;

    /// <summary>Slow update weight for same-axle cornering offsets.</summary>
    public float LocalBaselineLearningRate { get; set; } = 0.02f;

    /// <summary>Driven/reference excess required before wheelspin is meaningful.</summary>
    public float MinimumDrivenExcess { get; set; } = 0.08f;

    /// <summary>Individual-wheel excess required for affected-wheel evidence.</summary>
    public float MinimumIndividualExcess { get; set; } = 0.10f;

    /// <summary>Positive excess slope required to establish developing wheelspin.</summary>
    public float MinimumSpinDerivative { get; set; } = 0.50f;

    /// <summary>Minimum drop from the event peak before a collapse is considered.</summary>
    public float MinimumCollapse { get; set; } = 0.04f;

    /// <summary>Negative excess slope required for a rapid intervention candidate.</summary>
    public float MinimumCollapseRate { get; set; } = 1.50f;

    /// <summary>Confidence needed to mark a wheel-speed intervention confirmed.</summary>
    public float ConfirmationConfidence { get; set; } = 0.45f;

    public double ModulationWindowMilliseconds { get; set; } = 500;

    public double InterventionHoldMilliseconds { get; set; } = 120;

    public double RecoveryMilliseconds { get; set; } = 160;
}

/// <summary>
/// Sign handling for the acceleration axes.
///
/// FH6 documents car-local axes as X = right, Y = up, Z = forward, but the sign of the
/// reported values has to be confirmed against a recorded session before it can be
/// trusted. These flags are the place to record that confirmation: record a session of
/// straight-line acceleration and a steady left-hander, replay it, and flip whichever
/// axis reads backwards.
/// </summary>
public sealed class GForceSettings
{
    public bool InvertLateral { get; set; }

    public bool InvertLongitudinal { get; set; }

    public bool InvertVertical { get; set; }

    /// <summary>
    /// Acceleration that maps to the edge of the G indicator, in G. The 1G reference ring
    /// is drawn inside it.
    /// </summary>
    public float FullScaleG { get; set; } = 1.5f;
}

/// <summary>
/// Thresholds used to classify tyre state.
///
/// FH6 reports normalized slip where 0 means full grip and |value| &gt; 1 means loss of
/// grip; these are therefore slip thresholds, not "percentage grip" values.
/// </summary>
public sealed class GripSettings
{
    /// <summary>|slip| above which grip is considered lost.</summary>
    public float SlipLossThreshold { get; set; } = 1.0f;

    /// <summary>|slip| above which the tyre is considered to be approaching the limit.</summary>
    public float SlipApproachThreshold { get; set; } = 0.65f;

    /// <summary>|slip angle| above which lateral slip dominates.</summary>
    public float LateralSlipThreshold { get; set; } = 0.85f;

    /// <summary>Wheel speed (rad/s) below which a wheel is treated as locked rather than merely slow.</summary>
    public float LockedWheelSpeed { get; set; } = 2.0f;

    /// <summary>Vehicle speed (m/s) below which tyre telemetry is treated as noise.</summary>
    public float MinimumVehicleSpeed { get; set; } = 3.0f;

    /// <summary>How much faster than the other wheels a wheel must spin to count as wheelspin.</summary>
    public float WheelspinExcessRatio { get; set; } = 1.20f;
}

/// <summary>
/// Settings for power-curve capture and stored-curve RPM derivation.
/// FH6 does not transmit the useful powerband; it is derived from calibration data.
/// </summary>
public sealed class PowerbandSettings
{
    /// <summary>Resolution of captured and stored power curves, in RPM buckets.</summary>
    public int BinCount { get; set; } = 64;

    /// <summary>Minimum fractional deficit that makes a short recovery valley suspicious.</summary>
    public float PowerCurveDipFraction { get; set; } = 0.04f;

    /// <summary>Whether post-hoc raw-sample shape cleaning is enabled.</summary>
    public bool ShapeCleaningEnabled { get; set; } = true;

    /// <summary>Minimum throttle for a sample to be considered a valid power measurement.</summary>
    public float MinimumThrottle { get; set; } = 0.95f;

    /// <summary>Minimum vehicle speed (m/s) for a valid power sample.</summary>
    public float MinimumSpeed { get; set; } = 5f;

    /// <summary>Maximum total raw observations retained for this calibration run.</summary>
    public int MaximumRawSamples { get; set; } = 1024;

    /// <summary>Maximum recent observations retained in one RPM bin.</summary>
    public int MaximumSamplesPerBin { get; set; } = 64;

    /// <summary>Observations required before a bin is considered populated for confidence.</summary>
    public int MinimumSamplesPerBin { get; set; } = 3;

    /// <summary>Independent passes required before a populated bin is reliable.</summary>
    public int MinimumIndependentPassesPerBin { get; set; } = 2;

    /// <summary>Quantile used to estimate unrestricted power from each RPM bin.</summary>
    public float UpperPowerQuantile { get; set; } = 0.80f;

    /// <summary>Half-width of the robust local smoother in RPM.</summary>
    public float SmoothingWindowRpm { get; set; } = 350f;

    /// <summary>Number of robust local smoothing passes for the provisional curve.</summary>
    public int RobustSmoothingIterations { get; set; } = 2;

    /// <summary>Maximum raw-sample rejection/refit iterations.</summary>
    public int ShapeCleaningIterations { get; set; } = 3;

    /// <summary>Residual fraction below the provisional curve treated as suspicious evidence.</summary>
    public float SampleResidualThreshold { get; set; } = 0.05f;

    /// <summary>Maximum RPM width of a short cut-and-recovery valley.</summary>
    public float MaximumNarrowValleyRpmSpan { get; set; } = 400f;

    /// <summary>Power recovery fraction required after a suspicious valley.</summary>
    public float MinimumValleyRecoveryFraction { get; set; } = 0.95f;

    /// <summary>Maximum upper/lower quantile spread for a reliable bin.</summary>
    public float MaximumReliableSpreadFraction { get; set; } = 0.20f;

    /// <summary>Minimum normal RPM rate when timestamps provide one.</summary>
    public float MinimumRpmRate { get; set; }

    /// <summary>Maximum normal RPM rate when timestamps provide one.</summary>
    public float MaximumRpmRate { get; set; } = 20000f;

    /// <summary>Small same-gear RPM drop tolerated before treating a sample as a transient.</summary>
    public float MaximumRpmDrop { get; set; } = 50f;

    /// <summary>Relative RPM-rate change that is evidence of an abnormal acceleration response.</summary>
    public float RpmRateChangeFraction { get; set; } = 0.35f;

    /// <summary>Fraction of peak power that defines the main power band's lower and upper edges.</summary>
    public float PowerbandFraction { get; set; } = 0.90f;

    /// <summary>Fraction of telemetry max RPM used as the universal effective redline.</summary>
    public float EffectiveRedlinePercentile { get; set; } = 0.98f;

    /// <summary>Fraction of highest-RPM samples excluded from peak-power selection (0..1).</summary>
    public float PeakPowerTopPercentile { get; set; } = 0.05f;

    /// <summary>Fraction of highest-RPM samples excluded from powerband bounds (0..1).</summary>
    public float PowerbandTopPercentile { get; set; } = 0.05f;

    /// <summary>Fraction of top RPM samples to average when calculating the maximum practical RPM (0..1).</summary>
    public float PracticalRedlineTopPercentile { get; set; } = 0.05f;

    /// <summary>RPM spacing used when searching practical shift candidates.</summary>
    public float ShiftOptimizationCandidateStepRpm { get; set; } = 25f;

    /// <summary>Minimum measured acceleration samples required per gear for time optimization.</summary>
    public int MinimumShiftOptimizationSamplesPerGear { get; set; } = 8;

    /// <summary>Number of speed intervals used when integrating predicted shift time.</summary>
    public int ShiftOptimizationSpeedIntegrationSteps { get; set; } = 48;

}

public sealed class RecordingSettings
{
    public bool Enabled { get; set; }

    public string OutputDirectory { get; set; } = "sessions";
}

/// <summary>Persistent calibration controls. Raw session recording remains independent.</summary>
public sealed class CalibrationSettings
{
    /// <summary>Calibration JSON file name, resolved relative to the application directory.</summary>
    public string DataFile { get; set; } = "forzahud-calibration.json";

    /// <summary>Directory for compressed raw power samples, resolved relative to the application directory.</summary>
    public string RawSamplesDirectory { get; set; } = "forzahud-calibration-raw";

    /// <summary>Global hotkey used to toggle calibration recording.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>Whether calibration progress and results are printed to the terminal.</summary>
    public bool VerboseOutput { get; set; } = true;

    /// <summary>Opt-in calibration that learns from qualifying driving sessions automatically.</summary>
    public LiveCalibrationSettings Live { get; set; } = new();
}

/// <summary>Runtime and storage settings for automatic RPM calibration.</summary>
public sealed class LiveCalibrationSettings
{
    /// <summary>Whether calibration runs automatically when the player enters driving state.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether live power-curve collection may replace or extend an existing exact-identity record.</summary>
    public bool AllowOverwrite { get; set; }

    public LivePowerCurveSettings PowerCurve { get; set; } = new();

    public LiveGearShiftSettings GearShift { get; set; } = new();
}

/// <summary>Automatic power-curve collection thresholds.</summary>
public sealed class LivePowerCurveSettings
{
    /// <summary>Accepted power samples after which collection may finish.</summary>
    public int StopSampleCount { get; set; } = 120;

    /// <summary>RPM coverage required together with <see cref="StopSampleCount"/>.</summary>
    public float StopRpmCoverageFraction { get; set; } = 0.75f;

    /// <summary>Normalized overlap shape error above which a stored curve is replaced.</summary>
    public float OverwriteShapeErrorThreshold { get; set; } = 0.10f;
}

/// <summary>Automatic shift-ratio collection thresholds.</summary>
public sealed class LiveGearShiftSettings
{
    /// <summary>Shift observations batched before a live gear write; session exit flushes the remainder.</summary>
    public int MinimumSamplesPerGear { get; set; } = 4;

    /// <summary>Relative average-ratio error above which an existing gear is replaced.</summary>
    public float OverwriteRatioErrorThreshold { get; set; } = 0.05f;
}

/// <summary>
/// Placement and visibility of one HUD element.
/// </summary>
/// <param name="Enabled">Whether the element is drawn at all.</param>
/// <param name="X">Horizontal centre, normalized to the overlay width.</param>
/// <param name="Y">Vertical centre, normalized to the overlay height.</param>
/// <param name="Scale">Size multiplier applied to the element's own geometry.</param>
/// <param name="Opacity">Additional opacity multiplier, 0..1.</param>
public sealed record ElementSettings(
    bool Enabled = true,
    double X = 0.5,
    double Y = 0.5,
    double Scale = 1.0,
    double Opacity = 1.0);

/// <summary>
/// Built-in element placement, matching the reticle layout described in AGENTS.md.
/// </summary>
public static class DefaultElements
{
    /// <summary>Canonical element ids.</summary>
    public static class Ids
    {
        public const string Reticle = "reticle";
        public const string Steer = "steer";
        public const string Rpm = "rpm";
        public const string Boost = "boost";
        public const string Pedals = "pedals";
        public const string Gear = "gear";
        public const string Speed = "speed";
        public const string GForce = "gforce";
        public const string Tires = "tires";
        public const string Calibration = "calibration";
    }

    /// <summary>Fresh dictionary of the default layout.</summary>
    public static Dictionary<string, ElementSettings> Create() => new()
    {
        [Ids.Reticle] = new ElementSettings(true, 0.50, 0.50, 1.0),
        [Ids.Steer] = new ElementSettings(true, 0.50, 0.50, 1.0),
        // Arc meters share the reticle centre; their side, direction, and nesting are defined by ArcMeter.
        [Ids.Rpm] = new ElementSettings(true, 0.50, 0.50, 1.0),
        [Ids.Boost] = new ElementSettings(true, 0.50, 0.50, 1.0),
        [Ids.Pedals] = new ElementSettings(true, 0.50, 0.50, 1.0),
        [Ids.Gear] = new ElementSettings(true, 0.415, 0.50, 1.0),
        [Ids.Speed] = new ElementSettings(true, 0.585, 0.50, 1.0),
        [Ids.GForce] = new ElementSettings(true, 0.50, 0.79, 1.0),
        [Ids.Tires] = new ElementSettings(true, 0.50, 0.895, 1.0),
        [Ids.Calibration] = new ElementSettings(true, 0.50, 0.075, 1.0),
    };
}
