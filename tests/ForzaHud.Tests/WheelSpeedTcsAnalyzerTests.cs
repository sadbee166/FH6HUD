using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class WheelSpeedTcsAnalyzerTests
{
    [Fact]
    public void FwdOverspeedFollowedByRapidReconvergenceProducesInterventionEvidence()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.016, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.032, 50f, 50f, 50f, 50f));
        var spin = analyzer.Update(Snapshot(0.048, 60f, 60f, 50f, 50f));
        var correction = analyzer.Update(Snapshot(0.064, 51f, 51f, 50f, 50f));

        Assert.True(spin.SpinConfidence > 0f);
        Assert.Equal(WheelSpeedTcsState.Spinning, spin.State);
        Assert.True(correction.InterventionConfidence > 0f);
        Assert.True(correction.CombinedConfidence > 0f);
        Assert.Equal(WheelSpeedAxle.Front, correction.AffectedAxle);
        Assert.True(correction.AffectedWheels.FrontLeft);
        Assert.True(correction.AffectedWheels.FrontRight);
    }

    [Fact]
    public void WheelspinWithoutCollapseIsNotInterventionEvidence()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.016, 50f, 50f, 50f, 50f));
        var spin = analyzer.Update(Snapshot(0.032, 60f, 60f, 50f, 50f));
        var sustained = analyzer.Update(Snapshot(0.048, 60f, 60f, 50f, 50f));

        Assert.Equal(WheelSpeedTcsState.Spinning, spin.State);
        Assert.Equal(WheelSpeedTcsState.Spinning, sustained.State);
        Assert.True(sustained.SpinConfidence > 0f);
        Assert.Equal(0f, sustained.InterventionConfidence);
        Assert.Equal(0f, sustained.CombinedConfidence);
    }

    [Fact]
    public void OneDrivenWheelSpinIsAttributedToTheDrivenWheel()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.016, 50f, 50f, 50f, 50f));
        var spin = analyzer.Update(Snapshot(0.032, 60f, 50f, 50f, 50f));
        var correction = analyzer.Update(Snapshot(0.048, 50f, 50f, 50f, 50f));

        Assert.True(spin.SpinConfidence > 0f);
        Assert.True(spin.AffectedWheels.FrontLeft);
        Assert.False(spin.AffectedWheels.FrontRight);
        Assert.Equal(WheelSpeedAxle.Front, correction.AffectedAxle);
        Assert.True(correction.CombinedConfidence > 0f);
    }

    [Fact]
    public void LearnedFrontRearRatioPreventsNaturalAxleDifferenceFromBecomingSpin()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        for (var i = 0; i < 8; i++)
        {
            analyzer.Update(Snapshot(i * 0.016, 48.5f, 48.5f, 50f, 50f));
        }

        var stable = analyzer.Update(Snapshot(0.128, 48.5f, 48.5f, 50f, 50f));

        Assert.Equal(0f, stable.DrivenExcess, 3);
        Assert.Equal(0f, stable.CombinedConfidence);
        Assert.Equal(WheelSpeedTcsState.Normal, stable.State);
    }

    [Fact]
    public void SymmetricAwdSpeedIncreaseIsUnobservableRatherThanNegativeEvidence()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f, drivetrain: 2));
        var result = analyzer.Update(Snapshot(0.016, 70f, 70f, 70f, 70f, drivetrain: 2));

        Assert.False(result.Observable);
        Assert.Equal(0f, result.CombinedConfidence);
        Assert.Equal(WheelSpeedTcsState.Normal, result.State);
    }

    [Fact]
    public void TelemetryGapClearsPriorSpinHistory()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.016, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.032, 60f, 60f, 50f, 50f));
        var afterGap = analyzer.Update(Snapshot(0.300, 51f, 51f, 50f, 50f));

        Assert.Equal(WheelSpeedTcsState.Normal, afterGap.State);
        Assert.Equal(0f, afterGap.SpinConfidence);
        Assert.Equal(0f, afterGap.InterventionConfidence);
    }

    [Fact]
    public void RepeatedSpinCorrectionCyclesIncreaseModulationEvidence()
    {
        var analyzer = new WheelSpeedTcsAnalyzer(Settings());

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.016, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.032, 60f, 60f, 50f, 50f));
        analyzer.Update(Snapshot(0.048, 51f, 51f, 50f, 50f));
        analyzer.Update(Snapshot(0.064, 50f, 50f, 50f, 50f));
        analyzer.Update(Snapshot(0.080, 60f, 60f, 50f, 50f));
        var secondCorrection = analyzer.Update(Snapshot(0.096, 51f, 51f, 50f, 50f));

        Assert.Equal(WheelSpeedTcsState.InterventionConfirmed, secondCorrection.State);
        Assert.True(secondCorrection.ModulationConfidence > 0f);
        Assert.True(secondCorrection.CombinedConfidence >= secondCorrection.SpinConfidence
                    * secondCorrection.InterventionConfidence);
    }

    [Fact]
    public void OuterTcsFusionAppliesTheThrottleGateToWheelEvidence()
    {
        var settings = new TractionControlSettings
        {
            ActivationSamples = 1,
            AttackMilliseconds = 0,
            MinimumThrottle = 0.99f,
            WheelSpeed = Settings(),
        };
        var analyzer = new TractionControlAnalyzer(settings);

        analyzer.Update(Snapshot(0.000, 50f, 50f, 50f, 50f) with { Throttle = 0f });
        analyzer.Update(Snapshot(0.016, 50f, 50f, 50f, 50f) with { Throttle = 0f });
        analyzer.Update(Snapshot(0.032, 60f, 60f, 50f, 50f) with { Throttle = 0f });
        var result = analyzer.Update(Snapshot(0.048, 51f, 51f, 50f, 50f) with { Throttle = 0f });

        Assert.True(analyzer.WheelSpeedEvidence.CombinedConfidence > 0f);
        Assert.False(result);
        Assert.False(analyzer.PowerSampleAffectedByTcs);
    }

    private static WheelSpeedTcsSettings Settings() => new()
    {
        FilterAlpha = 1f,
        MinimumLearningSpeed = 1f,
        MinimumDrivenExcess = 0.05f,
        MinimumIndividualExcess = 0.05f,
        MinimumSpinDerivative = 0.1f,
        MinimumCollapse = 0.02f,
        MinimumCollapseRate = 0.5f,
        ConfirmationConfidence = 0.1f,
        BaselineLearningRate = 0.02f,
        LocalBaselineLearningRate = 0.02f,
    };

    private static TelemetrySnapshot Snapshot(
        double time,
        float frontLeft,
        float frontRight,
        float rearLeft,
        float rearRight,
        int drivetrain = 0) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineMaxRpm = 8000f,
            EngineIdleRpm = 1000f,
            CurrentEngineRpm = 4000f,
            Gear = 2,
            Speed = 20f,
            DrivetrainType = drivetrain,
            NumCylinders = 4,
            WheelRotationSpeedFrontLeft = frontLeft,
            WheelRotationSpeedFrontRight = frontRight,
            WheelRotationSpeedRearLeft = rearLeft,
            WheelRotationSpeedRearRight = rearRight,
            ReceivedAt = TimeSpan.FromSeconds(time),
        };
}
