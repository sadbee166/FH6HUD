using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class TractionControlAnalyzerTests
{
    [Fact]
    public void ReliableCurveActivatesAfterPersistentPowerDeficit()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 3,
            DeactivationSamples = 2,
        });
        Assert.False(analyzer.Update(Snapshot(0.00, 200f, 0.30f)));
        Assert.False(analyzer.PowerSampleAffectedByTcs);
        Assert.False(analyzer.Update(Snapshot(0.016, 200f, 0.40f)));
        Assert.True(analyzer.PowerSampleAffectedByTcs);
        Assert.False(analyzer.Update(Snapshot(0.032, 200f, 0.50f)));
        Assert.True(analyzer.PowerSampleAffectedByTcs);
        Assert.True(analyzer.Update(Snapshot(0.048, 200f, 0.60f)));
        Assert.True(analyzer.PowerSampleAffectedByTcs);
    }

    [Fact]
    public void WithoutCurveAStablePowerPlateauDoesNotActivateByItself()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
        });

        var powers = new[] { 300f, 304f, 305f, 302f, 300f, 298f };
        for (var i = 0; i < powers.Length; i++)
        {
            Assert.False(analyzer.Update(
                Snapshot(i * 0.016, powers[i], 0.05f, rpm: 3000f + i * 100f)));
        }
    }

    [Fact]
    public void WithoutCurveAnAbruptPowerCutActivates()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
        });

        Assert.False(analyzer.Update(Snapshot(0.00, 300f, 0.05f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 180f, 0.05f)));
        Assert.True(analyzer.Update(Snapshot(0.032, 100f, 0.05f)));
    }

    [Fact]
    public void WithoutCurveRepeatedCutAndRecoveryActivates()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
        });

        Assert.False(analyzer.Update(Snapshot(0.000, 300f, 0.05f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 180f, 0.05f)));
        Assert.False(analyzer.Update(Snapshot(0.032, 300f, 0.05f)));
        Assert.False(analyzer.Update(Snapshot(0.048, 180f, 0.05f)));
        Assert.True(analyzer.Update(Snapshot(0.064, 300f, 0.05f)));
    }

    [Fact]
    public void WithoutCurveSlipCorrelatedSuppressionActivates()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
        });

        Assert.False(analyzer.Update(Snapshot(0.00, 200f, 0.05f, rpm: 3000f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 208f, 0.10f, rpm: 3100f)));
        Assert.False(analyzer.Update(Snapshot(0.032, 208f, 0.25f, rpm: 3200f)));
        Assert.True(analyzer.Update(Snapshot(0.048, 208f, 0.35f, rpm: 3300f)));
    }

    [Fact]
    public void ShiftAndTelemetryGapDoNotCarryPowerCutEvidence()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
            ShiftSuppressionSamples = 2,
            ShiftSuppressionMilliseconds = 20,
        });

        Assert.False(analyzer.Update(Snapshot(0.00, 300f, 0.05f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 300f, 0.05f, gear: 3)));
        Assert.False(analyzer.Update(Snapshot(0.032, 150f, 0.40f, gear: 3)));
        Assert.False(analyzer.Update(Snapshot(0.20, 150f, 0.50f, gear: 3)));
    }

    [Fact]
    public void LimiterAndClutchTransientsAreExcluded()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
            ShiftSuppressionSamples = 2,
            ShiftSuppressionMilliseconds = 20,
        });

        Assert.False(analyzer.Update(Snapshot(0.00, 300f, 0.05f, rpm: 7000f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 100f, 0.40f, rpm: 7900f)));
        Assert.False(analyzer.Update(Snapshot(0.032, 100f, 0.50f, rpm: 7800f)));

        var clutchAnalyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
            ShiftSuppressionSamples = 2,
            ShiftSuppressionMilliseconds = 20,
        });
        Assert.False(clutchAnalyzer.Update(Snapshot(0.00, 300f, 0.05f)));
        Assert.False(clutchAnalyzer.Update(
            Snapshot(0.016, 100f, 0.40f) with { Clutch = 1f }));
        Assert.False(clutchAnalyzer.Update(Snapshot(0.032, 100f, 0.50f)));
    }

    [Fact]
    public void DeactivationAlsoUsesShortHysteresis()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 2,
            DeactivationSamples = 3,
        });
        Assert.False(analyzer.Update(Snapshot(0.00, 200f, 0.30f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 200f, 0.40f)));
        Assert.True(analyzer.Update(Snapshot(0.032, 200f, 0.40f)));
        Assert.True(analyzer.Update(Snapshot(0.048, 350f, 0.40f)));
        Assert.True(analyzer.Update(Snapshot(0.064, 350f, 0.40f)));
        Assert.False(analyzer.Update(Snapshot(0.080, 350f, 0.40f)));
    }

    [Fact]
    public void AttackAndReleaseDurationsGateStatusTransitions()
    {
        var analyzer = new TractionControlAnalyzer(new TractionControlSettings
        {
            ActivationSamples = 1,
            DeactivationSamples = 1,
            AttackMilliseconds = 100,
            ReleaseMilliseconds = 100,
        });
        Assert.False(analyzer.Update(Snapshot(0.000, 200f, 0.05f)));
        Assert.False(analyzer.Update(Snapshot(0.016, 200f, 0.25f)));
        Assert.False(analyzer.Update(Snapshot(0.096, 200f, 0.25f)));
        Assert.True(analyzer.Update(Snapshot(0.120, 200f, 0.25f)));
        Assert.True(analyzer.Update(Snapshot(0.136, 350f, 0.25f)));
        Assert.True(analyzer.Update(Snapshot(0.216, 350f, 0.25f)));
        Assert.False(analyzer.Update(Snapshot(0.240, 350f, 0.25f)));
    }

    [Fact]
    public void TireSlipActivationHonorsThrottleGate()
    {
        var settings = new TractionControlSettings
        {
            EnginePowerEvidenceEnabled = false,
            MinimumThrottle = 0.80f,
            MaximumThrottleDecrease = 0.05f,
            ActivationSamples = 1,
            AttackMilliseconds = 0,
            WheelSpeed = new WheelSpeedTcsSettings { Enabled = false },
            TireSlip = new TireSlipTcsSettings
            {
                AwdMinimumDrivenSlip = 0.20f,
                AwdActivationSamples = 1,
                DeactivationSamples = 1,
            },
        };
        var analyzer = new TractionControlAnalyzer(settings);

        var lowThrottle = Snapshot(0.000, 200f, 0.30f) with { Throttle = 0.70f };
        Assert.False(analyzer.Update(lowThrottle));
        Assert.False(analyzer.Update(lowThrottle with { ReceivedAt = TimeSpan.FromMilliseconds(16) }));

        analyzer.Reset();
        var fullThrottle = Snapshot(0.000, 200f, 0.30f);
        var sharpThrottleDrop = fullThrottle with
        {
            ReceivedAt = TimeSpan.FromMilliseconds(16),
            Throttle = 0.94f,
        };
        Assert.False(analyzer.Update(fullThrottle));
        Assert.False(analyzer.Update(sharpThrottleDrop));
        Assert.True(analyzer.Update(sharpThrottleDrop with { ReceivedAt = TimeSpan.FromMilliseconds(32) }));
    }

    [Fact]
    public void VehicleStateProcessorPublishesTheInferredTcsState()
    {
        var configuration = new HudConfiguration();
        configuration.Telemetry.Powerband.BinCount = 64;
        configuration.Telemetry.TractionControl.ActivationSamples = 2;
        configuration.Telemetry.TractionControl.ShiftSuppressionSamples = 8;
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-tcs-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                new VehicleIdentity(0, 0, 2, 6, 8000f),
                [
                    new PowerCurvePoint(2000f, 200f),
                    new PowerCurvePoint(3000f, 300f),
                    new PowerCurvePoint(4000f, 350f),
                    new PowerCurvePoint(5000f, 350f),
                    new PowerCurvePoint(6000f, 300f),
                    new PowerCurvePoint(7000f, 220f),
            ],
                new Dictionary<int, List<float>>() )));
            var processor = new VehicleStateProcessor(configuration, store);

            foreach (var (rpm, power) in new[]
            {
                (2000f, 200f),
                (3000f, 300f),
                (5000f, 350f),
                (6000f, 300f),
                (7000f, 220f),
                (4000f, 350f),
            })
            {
                processor.Process(Snapshot(0, power, 0.05f, rpm));
            }

            for (var i = 0; i < 9; i++)
            {
                processor.Process(Snapshot(0, 350f, 0.05f, 4000f));
            }

            var firstDeficit = processor.Process(Snapshot(0, 200f, 0.30f, 4000f));
            Assert.True(firstDeficit.Powerband.IsLearned);
            Assert.False(firstDeficit.TractionControlActive);
            Assert.True(processor.Process(Snapshot(0, 200f, 0.40f, 4000f)).TractionControlActive);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static TelemetrySnapshot Snapshot(
        double time,
        float power,
        float slip,
        float rpm = 4000f,
        int gear = 2) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineMaxRpm = 8000f,
            EngineIdleRpm = 1000f,
            CurrentEngineRpm = rpm,
            Power = power,
            Throttle = 1f,
            Speed = 20f,
            Gear = gear,
            DrivetrainType = 2,
            NumCylinders = 6,
            TireSlipRatioRearLeft = slip,
            TireSlipRatioRearRight = slip,
            ReceivedAt = TimeSpan.FromSeconds(time),
        };
}
