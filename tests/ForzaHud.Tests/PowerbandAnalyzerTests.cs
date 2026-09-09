using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class PowerbandAnalyzerTests
{
    [Fact]
    public void StoredCurveDerivesPowerbandAndShiftFromAverageRatio()
    {
        var settings = Settings();
        var identity = new VehicleIdentity(2871, 800, 2, 6, 8000f);
        var analyzer = new PowerbandAnalyzer(settings.BinCount);
        analyzer.UpdateVehicle(Frame(3000f, 100f, identity));

        var calibration = new VehicleCalibration(
            identity,
            [
                new PowerCurvePoint(2000f, 100f),
                new PowerCurvePoint(3000f, 290f),
                new PowerCurvePoint(4000f, 300f),
                new PowerCurvePoint(5000f, 280f),
                new PowerCurvePoint(7000f, 100f),
            ],
            new Dictionary<int, List<float>> { [1] = [0.5f, 0.6f] });

        analyzer.ApplyCalibration(calibration, settings);

        Assert.True(analyzer.State.IsLearned);
        Assert.Equal(4000f, analyzer.State.PeakPowerRpm);
        Assert.Contains(1, analyzer.State.ShiftRpms.Keys);

        var expected = new PowerbandAnalyzer(settings.BinCount);
        expected.UpdateVehicle(Frame(3000f, 100f, identity));
        expected.ApplyCalibration(
            calibration with { ShiftUpRpmDropRatioByGear = new Dictionary<int, List<float>> { [1] = [0.55f] } },
            settings);

        Assert.Equal(expected.State.ShiftRpm, analyzer.State.ShiftRpm);
    }

    private static PowerbandSettings Settings() => new()
    {
        BinCount = 64,
    };

    private static TelemetrySnapshot Frame(
        float rpm,
        float power,
        VehicleIdentity? identity = null) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 8000f,
            CurrentEngineRpm = rpm,
            Speed = 20f,
            Power = power,
            Throttle = 1f,
            Gear = 1,
            CarOrdinal = identity?.CarOrdinal ?? 2871,
            CarPerformanceIndex = identity?.CarPerformanceIndex ?? 800,
            DrivetrainType = identity?.DrivetrainType ?? 2,
            NumCylinders = identity?.NumCylinders ?? 6,
        };
}
