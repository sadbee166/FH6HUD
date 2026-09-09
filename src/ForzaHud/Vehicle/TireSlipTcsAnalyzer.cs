using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>
/// Detects sustained driven-tyre slip that is characteristic of TCS-equipped sessions.
/// Thresholds and attack times are drivetrain-specific because FWD, RWD, and AWD telemetry
/// expose different useful slip ranges.
/// </summary>
public sealed class TireSlipTcsAnalyzer
{
    private readonly TireSlipTcsSettings _settings;

    private VehicleIdentity? _identity;
    private bool _active;
    private int _activationSamples;
    private int _deactivationSamples;

    public TireSlipTcsAnalyzer(TireSlipTcsSettings settings) => _settings = settings;

    public bool Update(TelemetrySnapshot snapshot, bool throttleQualifying)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_settings.Enabled || !HasUsableFrame(snapshot))
        {
            Reset();
            return false;
        }

        var identity = VehicleIdentity.From(snapshot);
        if (_identity is { } previousIdentity && previousIdentity != identity)
        {
            Reset();
        }

        _identity = identity;

        var drivenSlip = DrivenWheelSlip(snapshot);
        var minimumSlip = MinimumSlip(snapshot.DrivetrainType);
        var evidence = drivenSlip >= minimumSlip && (throttleQualifying || _active);
        if (evidence)
        {
            _activationSamples++;
            _deactivationSamples = 0;
            if (!_active && _activationSamples >= ActivationSamples(snapshot.DrivetrainType))
            {
                _active = true;
            }
        }
        else
        {
            _activationSamples = 0;
            if (_active)
            {
                _deactivationSamples++;
                if (_deactivationSamples >= Math.Max(1, _settings.DeactivationSamples))
                {
                    _active = false;
                    _deactivationSamples = 0;
                }
            }
        }

        return _active;
    }

    public void Reset()
    {
        _identity = null;
        _active = false;
        _activationSamples = 0;
        _deactivationSamples = 0;
    }

    private float MinimumSlip(int drivetrain) => MinimumSlip(_settings, drivetrain);

    internal static float MinimumSlip(TireSlipTcsSettings settings, int drivetrain) => drivetrain switch
    {
        0 => settings.FwdMinimumDrivenSlip,
        1 => settings.RwdMinimumDrivenSlip,
        2 => settings.AwdMinimumDrivenSlip,
        _ => float.PositiveInfinity,
    };

    private int ActivationSamples(int drivetrain) => drivetrain switch
    {
        0 => _settings.FwdActivationSamples,
        1 => _settings.RwdActivationSamples,
        2 => _settings.AwdActivationSamples,
        _ => int.MaxValue,
    };

    private static bool HasUsableFrame(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm
        && snapshot.EngineMaxRpm > 0f
        && snapshot.CurrentEngineRpm >= snapshot.EngineIdleRpm
        && snapshot.CurrentEngineRpm <= snapshot.EngineMaxRpm
        && snapshot.Gear is >= 1 and <= 10
        && float.IsFinite(snapshot.TireSlipRatioFrontLeft)
        && float.IsFinite(snapshot.TireSlipRatioFrontRight)
        && float.IsFinite(snapshot.TireSlipRatioRearLeft)
        && float.IsFinite(snapshot.TireSlipRatioRearRight);

    private static float DrivenWheelSlip(TelemetrySnapshot snapshot)
    {
        var slip = snapshot.TireSlipRatio;
        return snapshot.DrivetrainType switch
        {
            0 => MathF.Max(MathF.Abs(slip.FrontLeft), MathF.Abs(slip.FrontRight)),
            1 => MathF.Max(MathF.Abs(slip.RearLeft), MathF.Abs(slip.RearRight)),
            2 => MathF.Max(
                MathF.Max(MathF.Abs(slip.FrontLeft), MathF.Abs(slip.FrontRight)),
                MathF.Max(MathF.Abs(slip.RearLeft), MathF.Abs(slip.RearRight))),
            _ => 0f,
        };
    }

}
