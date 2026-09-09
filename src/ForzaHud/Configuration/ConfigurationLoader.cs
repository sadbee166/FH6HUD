using System.Text.Json;
using System.Text.Json.Serialization;
using ForzaHud.Rendering;

namespace ForzaHud.Configuration;

/// <summary>
/// Reads the HUD configuration from JSON.
///
/// Failure is never fatal: a missing file means "use the defaults", and an unreadable
/// file reports the problem while still returning a usable configuration. There is no
/// graphical editor, so this file is the entire configuration surface.
/// </summary>
public static class ConfigurationLoader
{
    private const string FileName = "hud.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Result of a load attempt.</summary>
    /// <param name="Configuration">Always non-null: defaults, optionally overridden by the file.</param>
    /// <param name="SourcePath">File the configuration came from, or <see langword="null"/> when defaults were used.</param>
    /// <param name="Diagnostics">Human-readable problems encountered while loading.</param>
    public sealed record Result(HudConfiguration Configuration, string? SourcePath, IReadOnlyList<string> Diagnostics)
    {
        /// <summary>True when no problems were found.</summary>
        public bool IsClean => Diagnostics.Count == 0;

        /// <summary>Whether this result may replace an already running configuration.</summary>
        public bool CanApply { get; init; } = true;
    }

    /// <summary>
    /// Loads configuration from <paramref name="explicitPath"/>, or from the application
    /// directory, or from the current directory, in that order.
    /// </summary>
    public static Result Load(string? explicitPath = null)
    {
        var diagnostics = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return LoadFrom(explicitPath, diagnostics, reportMissing: true);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Directory.GetCurrentDirectory(), FileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return LoadFrom(candidate, diagnostics, reportMissing: false);
            }
        }

        diagnostics.Add($"No {FileName} found; using built-in defaults. " +
                        $"Copy one next to ForzaHud.exe to customise the HUD.");
        return new Result(new HudConfiguration(), null, diagnostics);
    }

    /// <summary>
    /// Writes a fully populated configuration file, including defaults for anything the
    /// user did not specify. Used by the --write-config switch to produce a starting point.
    /// </summary>
    public static void Write(string path, HudConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(configuration, WriteOptions));
    }

    private static Result LoadFrom(string path, List<string> diagnostics, bool reportMissing)
    {
        if (!File.Exists(path))
        {
            if (reportMissing)
            {
                diagnostics.Add($"Configuration file '{path}' does not exist; using built-in defaults.");
            }
            return new Result(new HudConfiguration(), null, diagnostics);
        }

        try
        {
            using var stream = File.OpenRead(path);
            var configuration = JsonSerializer.Deserialize<HudConfiguration>(stream, Options);

            if (configuration is null)
            {
                diagnostics.Add($"Configuration file '{path}' is empty; using built-in defaults.");
                return new Result(new HudConfiguration(), null, diagnostics);
            }

            Validate(configuration, diagnostics);
            return new Result(configuration, path, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add($"Configuration file '{path}' is not valid JSON: {exception.Message}");
            diagnostics.Add("Falling back to built-in defaults.");
            return new Result(new HudConfiguration(), null, diagnostics) { CanApply = false };
        }
        catch (IOException exception)
        {
            diagnostics.Add($"Configuration file '{path}' could not be read: {exception.Message}");
            diagnostics.Add("Falling back to built-in defaults.");
            return new Result(new HudConfiguration(), null, diagnostics) { CanApply = false };
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add($"Configuration file '{path}' could not be read: {exception.Message}");
            diagnostics.Add("Falling back to built-in defaults.");
            return new Result(new HudConfiguration(), null, diagnostics) { CanApply = false };
        }
    }

    private static void Validate(HudConfiguration configuration, List<string> diagnostics)
    {
        // JSON can explicitly contain null even though the model supplies non-null defaults.
        // Restore those sections before validating so a malformed file remains recoverable.
        configuration.Udp ??= new UdpSettings();
        configuration.Units ??= new UnitsSettings();
        configuration.Overlay ??= new OverlaySettings();
        configuration.Visual ??= new VisualSettings();
        configuration.Visual.Typography ??= new TypographySettings();
        configuration.Visual.PanelMotion ??= new PanelMotionSettings();
        configuration.Visual.PanelMotion.SpeedShake ??= new SpeedShakeSettings();
        configuration.Visual.Meters ??= new ArcMetersSettings();
        configuration.Visual.Opacity ??= new VisualOpacitySettings();
        configuration.Visual.Meters.Steer ??= new ArcMeterSettings();
        configuration.Visual.Meters.Rpm ??= new ArcMeterSettings();
        configuration.Visual.Meters.Boost ??= new ArcMeterSettings();
        configuration.Visual.Meters.Brake ??= new ArcMeterSettings();
        configuration.Visual.Meters.Throttle ??= new ArcMeterSettings();
        configuration.Visual.Theme ??= new ThemeSettings();
        configuration.Visual.Smoothing ??= new SmoothingSettings();
        configuration.Telemetry ??= new TelemetrySettings();
        configuration.Telemetry.GForce ??= new GForceSettings();
        configuration.Telemetry.Grip ??= new GripSettings();
        configuration.Telemetry.Powerband ??= new PowerbandSettings();
        configuration.Telemetry.TractionControl ??= new TractionControlSettings();
        configuration.Telemetry.TractionControl.WheelSpeed ??= new WheelSpeedTcsSettings();
        configuration.Telemetry.TractionControl.TireSlip ??= new TireSlipTcsSettings();
        configuration.Telemetry.TractionControl.Frame ??= new FrameTcsSettings();
        configuration.Recording ??= new RecordingSettings();
        configuration.Calibration ??= new CalibrationSettings();
        configuration.Calibration.Live ??= new LiveCalibrationSettings();
        configuration.Calibration.Live.PowerCurve ??= new LivePowerCurveSettings();
        configuration.Calibration.Live.GearShift ??= new LiveGearShiftSettings();
        configuration.Elements ??= DefaultElements.Create();

        if (!System.Net.IPAddress.TryParse(configuration.Udp.BindAddress, out _))
        {
            diagnostics.Add($"udp.bindAddress '{configuration.Udp.BindAddress}' is invalid; falling back to 127.0.0.1.");
            configuration.Udp.BindAddress = "127.0.0.1";
        }

        if (configuration.Udp.Port is < 1 or > 65535)
        {
            diagnostics.Add($"udp.port {configuration.Udp.Port} is out of range; falling back to 2247.");
            configuration.Udp.Port = 2247;
        }

        if (configuration.Udp.Port is >= 5200 and <= 5300)
        {
            diagnostics.Add("udp.port is inside 5200-5300, which FH6 uses for its own outgoing socket. Choose another port.");
        }

        if (configuration.Overlay.Opacity is < 0 or > 1)
        {
            diagnostics.Add("overlay.opacity must be between 0 and 1; falling back to 1.0.");
            configuration.Overlay.Opacity = 1.0;
        }

        if (configuration.Visual.ReticleRadius <= 0)
        {
            diagnostics.Add("visual.reticleRadius must be positive; falling back to 0.30.");
            configuration.Visual.ReticleRadius = 0.30f;
        }

        ValidatePanelMotion(configuration.Visual.PanelMotion, diagnostics);

        ValidateMeter("visual.meters.steer", configuration.Visual.Meters.Steer, diagnostics);
        ValidateMeter("visual.meters.rpm", configuration.Visual.Meters.Rpm, diagnostics);
        ValidateMeter("visual.meters.boost", configuration.Visual.Meters.Boost, diagnostics);
        ValidateMeter("visual.meters.brake", configuration.Visual.Meters.Brake, diagnostics, maximumStartValue: 1f);
        ValidateMeter("visual.meters.throttle", configuration.Visual.Meters.Throttle, diagnostics, maximumStartValue: 1f);

        if (configuration.Telemetry.GForce.FullScaleG <= 0)
        {
            diagnostics.Add("telemetry.gForce.fullScaleG must be positive; falling back to 1.5.");
            configuration.Telemetry.GForce.FullScaleG = 1.5f;
        }

        if (configuration.Overlay.TargetFramesPerSecond is < 10 or > 480)
        {
            diagnostics.Add("overlay.targetFramesPerSecond must be between 10 and 480; falling back to 120.");
            configuration.Overlay.TargetFramesPerSecond = 120;
        }

        ValidatePowerband(configuration.Telemetry.Powerband, diagnostics);
        ValidateTractionControl(configuration.Telemetry.TractionControl, diagnostics);

        if (string.IsNullOrWhiteSpace(configuration.Calibration.DataFile))
        {
            diagnostics.Add("calibration.dataFile must not be empty; falling back to forzahud-calibration.json.");
            configuration.Calibration.DataFile = "forzahud-calibration.json";
        }

        if (string.IsNullOrWhiteSpace(configuration.Calibration.RawSamplesDirectory))
        {
            diagnostics.Add("calibration.rawSamplesDirectory must not be empty; falling back to forzahud-calibration-raw.");
            configuration.Calibration.RawSamplesDirectory = "forzahud-calibration-raw";
        }

        if (string.IsNullOrWhiteSpace(configuration.Calibration.CarOrdinalNamesFile))
        {
            diagnostics.Add("calibration.carOrdinalNamesFile must not be empty; falling back to Forza Horizon 6 Car Ordinals.json.");
            configuration.Calibration.CarOrdinalNamesFile = "Forza Horizon 6 Car Ordinals.json";
        }

        if (string.IsNullOrWhiteSpace(configuration.Calibration.ToggleHotkey))
        {
            diagnostics.Add("calibration.toggleHotkey must not be empty; falling back to Ctrl+Alt+R.");
            configuration.Calibration.ToggleHotkey = "Ctrl+Alt+R";
        }

        ValidateLiveCalibration(configuration.Calibration.Live, diagnostics);

        var defaults = DefaultElements.Create();
        foreach (var (id, settings) in configuration.Elements.ToArray())
        {
            if (!defaults.ContainsKey(id))
            {
                diagnostics.Add($"Unknown element '{id}' in configuration. Known elements: {string.Join(", ", defaults.Keys)}.");
                continue;
            }

            if (settings is null)
            {
                diagnostics.Add($"Element '{id}' is null; falling back to its built-in default.");
                configuration.Elements[id] = defaults[id];
                continue;
            }

            if (settings.Scale <= 0)
            {
                diagnostics.Add($"Element '{id}' has a non-positive scale; falling back to 1.0.");
                configuration.Elements[id] = settings with { Scale = 1.0 };
            }
        }
    }

    private static void ValidateMeter(
        string path,
        ArcMeterSettings settings,
        List<string> diagnostics,
        float? maximumStartValue = null)
    {
        if (settings.ArcLength <= 0 || settings.ArcLength > 360)
        {
            diagnostics.Add($"{path}.arcLength must be greater than 0 and at most 360; falling back to 90.");
            settings.ArcLength = 90f;
        }

        if (settings.Width <= 0)
        {
            diagnostics.Add($"{path}.width must be positive; falling back to 1.6.");
            settings.Width = 1.6f;
        }

        if (settings.StartValue < 0 || (maximumStartValue.HasValue && settings.StartValue > maximumStartValue.Value))
        {
            var range = maximumStartValue.HasValue ? $" between 0 and {maximumStartValue.Value:0.###}" : " non-negative";
            diagnostics.Add($"{path}.startValue must be{range}; falling back to 0.");
            settings.StartValue = 0f;
        }

        if (settings.YellowZoneOffsetPercent is < 0 or > 100)
        {
            diagnostics.Add($"{path}.yellowZoneOffsetPercent must be between 0 and 100; falling back to 0.");
            settings.YellowZoneOffsetPercent = 0f;
        }

        if (settings.RedZoneOffsetPercent is < 0 or > 100)
        {
            diagnostics.Add($"{path}.redZoneOffsetPercent must be between 0 and 100; falling back to 0.");
            settings.RedZoneOffsetPercent = 0f;
        }
    }

    private static void ValidatePanelMotion(PanelMotionSettings settings, List<string> diagnostics)
    {
        if (settings.SmoothingMilliseconds < 0)
        {
            diagnostics.Add("visual.panelMotion.smoothingMilliseconds must be non-negative; falling back to 120.");
            settings.SmoothingMilliseconds = 120;
        }

        if (settings.MinimumScale < 0)
        {
            diagnostics.Add("visual.panelMotion.minimumScale must be non-negative; falling back to 0.");
            settings.MinimumScale = 0f;
        }

        if (settings.MaximumScale < settings.MinimumScale)
        {
            diagnostics.Add("visual.panelMotion.maximumScale must be at least minimumScale; falling back to 1.25.");
            settings.MaximumScale = Math.Max(1.25f, settings.MinimumScale);
        }

        if (settings.YawPivotX is < 0 or > 1)
        {
            diagnostics.Add("visual.panelMotion.yawPivotX must be between 0 and 1; falling back to 0.5.");
            settings.YawPivotX = 0.5f;
        }

        if (settings.PitchPivotY is < 0 or > 1)
        {
            diagnostics.Add("visual.panelMotion.pitchPivotY must be between 0 and 1; falling back to 0.5.");
            settings.PitchPivotY = 0.5f;
        }

        if (settings.MaximumYawRotationDegrees < 0)
        {
            diagnostics.Add("visual.panelMotion.maximumYawRotationDegrees must be non-negative; falling back to 12.");
            settings.MaximumYawRotationDegrees = 12f;
        }

        if (settings.MaximumPitchRotationDegrees < 0)
        {
            diagnostics.Add("visual.panelMotion.maximumPitchRotationDegrees must be non-negative; falling back to 12.");
            settings.MaximumPitchRotationDegrees = 12f;
        }

        ValidateSpeedShake(settings.SpeedShake, diagnostics);
    }

    private static void ValidateSpeedShake(SpeedShakeSettings settings, List<string> diagnostics)
    {
        if (settings.StartSpeedKph < 0)
        {
            diagnostics.Add("visual.panelMotion.speedShake.startSpeedKph must be non-negative; falling back to 72.");
            settings.StartSpeedKph = 72f;
        }

        if (settings.FullStrengthSpeedKph <= settings.StartSpeedKph)
        {
            diagnostics.Add("visual.panelMotion.speedShake.fullStrengthSpeedKph must be greater than startSpeedKph; falling back to 216.");
            settings.FullStrengthSpeedKph = 216f;
        }

        if (settings.Amplitude < 0)
        {
            diagnostics.Add("visual.panelMotion.speedShake.amplitude must be non-negative; falling back to 0.006.");
            settings.Amplitude = 0.006f;
        }

        if (settings.FrequencyHz < 0)
        {
            diagnostics.Add("visual.panelMotion.speedShake.frequencyHz must be non-negative; falling back to 16.");
            settings.FrequencyHz = 16f;
        }
    }

    private static void ValidatePowerband(PowerbandSettings settings, List<string> diagnostics)
    {
        if (settings.BinCount is < 8 or > 256)
        {
            diagnostics.Add("telemetry.powerband.binCount must be between 8 and 256; falling back to 64.");
            settings.BinCount = 64;
        }

        if (settings.PowerCurveDipFraction is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.powerCurveDipFraction must be between 0 and 1; falling back to 0.04.");
            settings.PowerCurveDipFraction = 0.04f;
        }

        if (settings.MinimumThrottle is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.minimumThrottle must be between 0 and 1; falling back to 0.95.");
            settings.MinimumThrottle = 0.95f;
        }

        if (settings.MinimumSpeed < 0)
        {
            diagnostics.Add("telemetry.powerband.minimumSpeed must be non-negative; falling back to 5.");
            settings.MinimumSpeed = 5f;
        }

        if (settings.MaximumRawSamples < 1)
        {
            diagnostics.Add("telemetry.powerband.maximumRawSamples must be positive; falling back to 1024.");
            settings.MaximumRawSamples = 1024;
        }

        if (settings.MaximumSamplesPerBin < 1)
        {
            diagnostics.Add("telemetry.powerband.maximumSamplesPerBin must be positive; falling back to 64.");
            settings.MaximumSamplesPerBin = 64;
        }

        if (settings.MinimumSamplesPerBin < 1)
        {
            diagnostics.Add("telemetry.powerband.minimumSamplesPerBin must be positive; falling back to 3.");
            settings.MinimumSamplesPerBin = 3;
        }

        if (settings.MinimumIndependentPassesPerBin < 1)
        {
            diagnostics.Add("telemetry.powerband.minimumIndependentPassesPerBin must be positive; falling back to 2.");
            settings.MinimumIndependentPassesPerBin = 2;
        }

        if (settings.UpperPowerQuantile is <= 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.upperPowerQuantile must be greater than 0 and at most 1; falling back to 0.80.");
            settings.UpperPowerQuantile = 0.80f;
        }

        if (settings.SmoothingWindowRpm <= 0)
        {
            diagnostics.Add("telemetry.powerband.smoothingWindowRpm must be positive; falling back to 350.");
            settings.SmoothingWindowRpm = 350f;
        }

        if (settings.RobustSmoothingIterations is < 1 or > 8)
        {
            diagnostics.Add("telemetry.powerband.robustSmoothingIterations must be between 1 and 8; falling back to 2.");
            settings.RobustSmoothingIterations = 2;
        }

        if (settings.ShapeCleaningIterations is < 1 or > 8)
        {
            diagnostics.Add("telemetry.powerband.shapeCleaningIterations must be between 1 and 8; falling back to 3.");
            settings.ShapeCleaningIterations = 3;
        }

        if (settings.SampleResidualThreshold is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.sampleResidualThreshold must be between 0 and 1; falling back to 0.05.");
            settings.SampleResidualThreshold = 0.05f;
        }

        if (settings.MaximumNarrowValleyRpmSpan <= 0)
        {
            diagnostics.Add("telemetry.powerband.maximumNarrowValleyRpmSpan must be positive; falling back to 400.");
            settings.MaximumNarrowValleyRpmSpan = 400f;
        }

        if (settings.MinimumValleyRecoveryFraction is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.minimumValleyRecoveryFraction must be between 0 and 1; falling back to 0.95.");
            settings.MinimumValleyRecoveryFraction = 0.95f;
        }

        if (settings.MaximumReliableSpreadFraction is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.maximumReliableSpreadFraction must be between 0 and 1; falling back to 0.20.");
            settings.MaximumReliableSpreadFraction = 0.20f;
        }

        if (settings.MinimumRpmRate < 0)
        {
            diagnostics.Add("telemetry.powerband.minimumRpmRate must be non-negative; falling back to 0.");
            settings.MinimumRpmRate = 0f;
        }

        if (settings.MaximumRpmRate <= 0)
        {
            diagnostics.Add("telemetry.powerband.maximumRpmRate must be positive; falling back to 20000.");
            settings.MaximumRpmRate = 20000f;
        }

        if (settings.MaximumRpmDrop < 0)
        {
            diagnostics.Add("telemetry.powerband.maximumRpmDrop must be non-negative; falling back to 50.");
            settings.MaximumRpmDrop = 50f;
        }

        if (settings.RpmRateChangeFraction is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.rpmRateChangeFraction must be between 0 and 1; falling back to 0.35.");
            settings.RpmRateChangeFraction = 0.35f;
        }

        if (settings.PowerbandFraction is <= 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.powerbandFraction must be greater than 0 and at most 1; falling back to 0.90.");
            settings.PowerbandFraction = 0.90f;
        }

        if (settings.EffectiveRedlinePercentile is <= 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.effectiveRedlinePercentile must be greater than 0 and at most 1; falling back to 0.98.");
            settings.EffectiveRedlinePercentile = 0.98f;
        }

        if (settings.PeakPowerTopPercentile is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.peakPowerTopPercentile must be between 0 and 1; falling back to 0.05.");
            settings.PeakPowerTopPercentile = 0.05f;
        }

        if (settings.PowerbandTopPercentile is < 0 or > 1)
        {
            diagnostics.Add("telemetry.powerband.powerbandTopPercentile must be between 0 and 1; falling back to 0.05.");
            settings.PowerbandTopPercentile = 0.05f;
        }

        if (settings.ShiftOptimizationCandidateStepRpm <= 0)
        {
            diagnostics.Add("telemetry.powerband.shiftOptimizationCandidateStepRpm must be positive; falling back to 25.");
            settings.ShiftOptimizationCandidateStepRpm = 25f;
        }

        if (settings.MinimumShiftOptimizationSamplesPerGear < 2)
        {
            diagnostics.Add("telemetry.powerband.minimumShiftOptimizationSamplesPerGear must be at least 2; falling back to 8.");
            settings.MinimumShiftOptimizationSamplesPerGear = 8;
        }

        if (settings.ShiftOptimizationSpeedIntegrationSteps < 1)
        {
            diagnostics.Add("telemetry.powerband.shiftOptimizationSpeedIntegrationSteps must be positive; falling back to 48.");
            settings.ShiftOptimizationSpeedIntegrationSteps = 48;
        }

    }

    private static void ValidateTractionControl(
        TractionControlSettings settings,
        List<string> diagnostics)
    {
        var frame = settings.Frame;
        if (frame.RegionX < 0f || frame.RegionY < 0f
            || frame.RegionWidth <= 0f || frame.RegionHeight <= 0f
            || frame.RegionX + frame.RegionWidth > 1f
            || frame.RegionY + frame.RegionHeight > 1f)
        {
            diagnostics.Add("telemetry.tractionControl.frame region must fit within the monitor; falling back to the supplied 2560x1440 reference region.");
            frame.RegionX = 0.950f;
            frame.RegionY = 0.925f;
            frame.RegionWidth = 0.025f;
            frame.RegionHeight = 0.020f;
        }

        if (frame.MinimumOnPixels < 1)
        {
            diagnostics.Add("telemetry.tractionControl.frame.minimumOnPixels must be positive; falling back to 20.");
            frame.MinimumOnPixels = 20;
        }

        if (frame.MinimumCyanChannel is < 0 or > 255)
        {
            diagnostics.Add("telemetry.tractionControl.frame.minimumCyanChannel must be between 0 and 255; falling back to 120.");
            frame.MinimumCyanChannel = 120;
        }

        if (frame.MinimumCyanDominance is < 0 or > 255)
        {
            diagnostics.Add("telemetry.tractionControl.frame.minimumCyanDominance must be between 0 and 255; falling back to 50.");
            frame.MinimumCyanDominance = 50;
        }

        if (settings.AttackMilliseconds < 0)
        {
            diagnostics.Add("telemetry.tractionControl.attackMilliseconds must be non-negative; falling back to 10.");
            settings.AttackMilliseconds = 10;
        }

        if (settings.ReleaseMilliseconds < 0)
        {
            diagnostics.Add("telemetry.tractionControl.releaseMilliseconds must be non-negative; falling back to 30.");
            settings.ReleaseMilliseconds = 30;
        }
    }

    private static void ValidateLiveCalibration(LiveCalibrationSettings settings, List<string> diagnostics)
    {
        if (settings.PowerCurve.StopSampleCount < 1)
        {
            diagnostics.Add("calibration.live.powerCurve.stopSampleCount must be positive; falling back to 120.");
            settings.PowerCurve.StopSampleCount = 120;
        }

        if (settings.PowerCurve.StopRpmCoverageFraction is <= 0 or > 1)
        {
            diagnostics.Add("calibration.live.powerCurve.stopRpmCoverageFraction must be greater than 0 and at most 1; falling back to 0.75.");
            settings.PowerCurve.StopRpmCoverageFraction = 0.75f;
        }

        if (settings.PowerCurve.OverwriteShapeErrorThreshold is < 0 or > 1)
        {
            diagnostics.Add("calibration.live.powerCurve.overwriteShapeErrorThreshold must be between 0 and 1; falling back to 0.10.");
            settings.PowerCurve.OverwriteShapeErrorThreshold = 0.10f;
        }

        if (settings.GearShift.MinimumSamplesPerGear < 1)
        {
            diagnostics.Add("calibration.live.gearShift.minimumSamplesPerGear must be positive; falling back to 4.");
            settings.GearShift.MinimumSamplesPerGear = 4;
        }

        if (settings.GearShift.OverwriteRatioErrorThreshold is < 0 or > 1)
        {
            diagnostics.Add("calibration.live.gearShift.overwriteRatioErrorThreshold must be between 0 and 1; falling back to 0.05.");
            settings.GearShift.OverwriteRatioErrorThreshold = 0.05f;
        }
    }

}
