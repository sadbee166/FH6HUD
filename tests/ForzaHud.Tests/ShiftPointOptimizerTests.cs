using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class ShiftPointOptimizerTests
{
    [Fact]
    public void ShiftPointUsesMeasuredTimeWhenLaterShiftIsFaster()
    {
        var settings = new PowerbandSettings
        {
            BinCount = 64,
            PracticalRedlineTopPercentile = 0.01f,
            ShiftOptimizationCandidateStepRpm = 100f,
            MinimumShiftOptimizationSamplesPerGear = 3,
            ShiftOptimizationSpeedIntegrationSteps = 32,
        };
        var identity = new VehicleIdentity(2871, 800, 2, 6, 8000f);
        var analyzer = new PowerbandAnalyzer(settings.BinCount);
        analyzer.UpdateVehicle(Frame(identity));

        var raw = new List<PowerCurveSample>();
        for (var speed = 40f; speed <= 100f; speed += 5f)
        {
            raw.Add(Sample(speed * 100f, speed, 10f, 1));
            raw.Add(Sample(speed * 80f, speed, 5f, 2));
        }

        var calibration = new VehicleCalibration(
            identity,
            [
                new PowerCurvePoint(4000f, 300f),
                new PowerCurvePoint(6000f, 260f),
                new PowerCurvePoint(7800f, 100f),
            ],
            new Dictionary<int, List<float>> { [1] = [0.8f] })
        {
            RawPowerSamples = raw,
            ShiftDurationMilliseconds = [250f],
        };

        analyzer.ApplyCalibration(calibration, settings);

        Assert.True(analyzer.State.ShiftRpm >= 7600f);
    }

    private static PowerCurveSample Sample(float rpm, float speed, float acceleration, int gear) =>
        new(rpm, 200f, 1f, gear, 1000f, 0f, TimeSpan.FromSeconds(speed))
        {
            SpeedMetersPerSecond = speed,
            LongitudinalAcceleration = acceleration,
        };

    private static TelemetrySnapshot Frame(VehicleIdentity identity) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineIdleRpm = 1000f,
            EngineMaxRpm = identity.MaxRpm,
            CurrentEngineRpm = 4000f,
            Speed = 40f,
            Power = 200f,
            Throttle = 1f,
            Gear = 1,
            CarOrdinal = identity.CarOrdinal,
            CarPerformanceIndex = identity.CarPerformanceIndex,
            DrivetrainType = identity.DrivetrainType,
            NumCylinders = identity.NumCylinders,
        };
}
