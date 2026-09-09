using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class VehicleStateProcessorTests
{
    [Fact]
    public void NonDrivingFrameRetainsLastValidRpmRangeAlongsideLoadedBand()
    {
        var configuration = new HudConfiguration();
        configuration.Telemetry.Powerband.BinCount = 64;
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-state-{Guid.NewGuid():N}.json");
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
            DerivedState? learned = null;

            foreach (var sample in PowerSamples())
            {
                learned = processor.Process(sample);
            }

            Assert.NotNull(learned);
            Assert.True(learned.Powerband.IsLearned);

            var afterMenuFrame = processor.Process(TelemetrySnapshot.Empty);

            Assert.Equal(learned.Powerband, afterMenuFrame.Powerband);
            Assert.Equal(learned.RpmRange, afterMenuFrame.RpmRange);
            Assert.True(afterMenuFrame.RpmRange.IsValid);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void UncalibratedTelemetryDoesNotCreatePowerband()
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());

        foreach (var sample in PowerSamples())
        {
            processor.Process(sample);
        }

        var state = processor.Process(TelemetrySnapshot.Empty);

        Assert.False(state.Powerband.IsLearned);
        Assert.Equal(PowerbandState.Empty, state.Powerband);
    }

    [Theory]
    [InlineData(0, true, "R")]
    [InlineData(0, false, "-")]
    [InlineData(11, true, "N")]
    [InlineData(11, false, "-")]
    [InlineData(1, true, "1")]
    [InlineData(2, true, "2")]
    [InlineData(6, true, "6")]
    [InlineData(10, true, "10")]
    public void GearLabelsFormatCorrectlyAcrossAllGears(int gear, bool isRaceOn, string expectedLabel)
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());
        var snapshot = TelemetrySnapshot.Empty with
        {
            IsRaceOn = isRaceOn,
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 8000f,
            CurrentEngineRpm = 2000f,
            Gear = gear,
        };

        var state = processor.Process(snapshot);
        Assert.Equal(expectedLabel, state.GearLabel);
    }

    [Fact]
    public void VehicleWithZeroCylindersIsClassifiedAsEvAndDisablesRpmCalculations()
    {
        var configuration = new HudConfiguration();
        configuration.Telemetry.Powerband.BinCount = 64;
        var output = new List<string>();
        var processor = new VehicleStateProcessor(configuration, calibrationOutput: output.Add);

        DerivedState? evState = null;
        foreach (var sample in PowerSamples(numCylinders: 0))
        {
            evState = processor.Process(sample);
        }

        Assert.NotNull(evState);
        Assert.True(evState.IsElectric);
        Assert.False(evState.Powerband.IsLearned);
        Assert.Equal(PowerbandState.Empty, evState.Powerband);

        var toggleResult = processor.ToggleCalibrationRecording();
        Assert.False(toggleResult.IsRecording);
        Assert.False(toggleResult.Saved);
    }

    [Fact]
    public void VehicleWithNonZeroCylindersIsNotClassifiedAsEv()
    {
        var processor = new VehicleStateProcessor(new HudConfiguration());
        var snapshot = TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 8000f,
            CurrentEngineRpm = 2000f,
            NumCylinders = 4,
        };

        var state = processor.Process(snapshot);
        Assert.False(state.IsElectric);
    }

    private static IEnumerable<TelemetrySnapshot> PowerSamples(int numCylinders = 6)
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
                    NumCylinders = numCylinders,
                };
            }
        }
    }
}
