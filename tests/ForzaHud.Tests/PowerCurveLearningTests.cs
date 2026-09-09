using ForzaHud.Configuration;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class PowerCurveLearningTests
{
    [Fact]
    public void RecorderUsesTheConfiguredUpperPowerQuantileForEachBin()
    {
        var settings = Settings();
        settings.BinCount = 8;
        settings.UpperPowerQuantile = 0.80f;
        settings.MinimumSamplesPerBin = 1;
        settings.MinimumIndependentPassesPerBin = 1;
        settings.SmoothingWindowRpm = 1f;
        settings.ShapeCleaningIterations = 1;
        settings.SampleResidualThreshold = 1f;
        settings.PowerCurveDipFraction = 1f;
        var recorder = new CalibrationRecorder(settings);

        recorder.Start(Snapshot(2000f, 100f));
        recorder.Record(Snapshot(2100f, 200f));
        recorder.Record(Snapshot(2200f, 300f));

        Assert.True(recorder.TryGetPowerCurve(out var curve));
        Assert.Equal(260f, Assert.Single(curve).Power, precision: 3);
        Assert.Equal(3, recorder.RawPowerSamples.Count);
    }

    [Fact]
    public void ShapeCleaningRemovesTheRawSamplesInsideANarrowRecoveryValley()
    {
        var settings = Settings();
        settings.BinCount = 40;
        settings.MinimumSamplesPerBin = 1;
        settings.MinimumIndependentPassesPerBin = 1;
        settings.UpperPowerQuantile = 0.80f;
        settings.SmoothingWindowRpm = 400f;
        settings.ShapeCleaningIterations = 3;
        settings.SampleResidualThreshold = 0.05f;
        settings.PowerCurveDipFraction = 0.03f;
        settings.MaximumNarrowValleyRpmSpan = 500f;
        settings.MinimumValleyRecoveryFraction = 0.95f;
        var recorder = new CalibrationRecorder(settings);
        var samples = new[]
        {
            (6500f, 300f),
            (6600f, 300f),
            (6700f, 300f),
            (6800f, 300f),
            (6900f, 300f),
            (7000f, 300f),
            (7100f, 300f),
            (7200f, 230f),
            (7300f, 220f),
            (7400f, 300f),
            (7500f, 300f),
            (7600f, 290f),
            (7700f, 280f),
            (7800f, 270f),
        };

        recorder.Start(Snapshot(samples[0].Item1, samples[0].Item2, milliseconds: 0));
        for (var index = 1; index < samples.Length; index++)
        {
            recorder.Record(Snapshot(
                samples[index].Item1,
                samples[index].Item2,
                milliseconds: index * 16));
        }

        Assert.True(recorder.TryGetPowerCurve(out var curve));
        Assert.DoesNotContain(recorder.RawPowerSamples, sample => sample.Rpm is 7200f or 7300f);
        Assert.DoesNotContain(curve, point => point.Rpm is >= 7200f and <= 7400f && point.Power < 270f);
    }

    [Fact]
    public void ShapeCleaningCanBeDisabled()
    {
        var settings = Settings();
        settings.BinCount = 40;
        settings.MinimumSamplesPerBin = 1;
        settings.MinimumIndependentPassesPerBin = 1;
        settings.UpperPowerQuantile = 0.80f;
        settings.SmoothingWindowRpm = 400f;
        settings.ShapeCleaningEnabled = false;
        settings.ShapeCleaningIterations = 3;
        settings.SampleResidualThreshold = 0.05f;
        settings.PowerCurveDipFraction = 0.03f;
        settings.MaximumNarrowValleyRpmSpan = 500f;
        settings.MinimumValleyRecoveryFraction = 0.95f;
        var recorder = new CalibrationRecorder(settings);
        var samples = new[]
        {
            (6500f, 300f),
            (6600f, 300f),
            (6700f, 300f),
            (6800f, 300f),
            (6900f, 300f),
            (7000f, 300f),
            (7100f, 300f),
            (7200f, 230f),
            (7300f, 220f),
            (7400f, 300f),
            (7500f, 300f),
            (7600f, 290f),
            (7700f, 280f),
            (7800f, 270f),
        };

        recorder.Start(Snapshot(samples[0].Item1, samples[0].Item2, milliseconds: 0));
        for (var index = 1; index < samples.Length; index++)
        {
            recorder.Record(Snapshot(
                samples[index].Item1,
                samples[index].Item2,
                milliseconds: index * 16));
        }

        Assert.True(recorder.TryGetPowerCurve(out _));
        Assert.Equal(0, recorder.RejectedPowerSamples);
        Assert.Contains(recorder.RawPowerSamples, sample => sample.Rpm is 7200f or 7300f);
    }

    [Fact]
    public void TcsAffectedFramesAreExcludedBeforeTheyReachTheRecorder()
    {
        var settings = Settings();
        var tcsSettings = new TractionControlSettings
        {
            ActivationSamples = 1,
            DeactivationSamples = 1,
            AttackMilliseconds = 0,
            ReleaseMilliseconds = 0,
            MinimumDrivenSlip = 0.10f,
            MinimumDrivenSlipIncrease = 0.02f,
        };
        var tcs = new TractionControlAnalyzer(tcsSettings);
        var recorder = new CalibrationRecorder(settings, tcsSettings);
        var initial = Snapshot(2000f, 100f, 0f);

        tcs.Update(initial);
        recorder.Start(initial, includeInitialPowerSample: !tcs.PowerSampleAffectedByTcs);

        var affected = Snapshot(2100f, 100f, 0.20f, 16);
        tcs.Update(affected);
        Assert.True(tcs.PowerSampleAffectedByTcs);
        recorder.Record(affected, includePowerSample: !tcs.PowerSampleAffectedByTcs);

        var stillAffected = Snapshot(2200f, 100f, 0.20f, 32);
        tcs.Update(stillAffected);
        Assert.True(tcs.PowerSampleAffectedByTcs);
        recorder.Record(stillAffected, includePowerSample: !tcs.PowerSampleAffectedByTcs);

        var recovered = Snapshot(2300f, 300f, 0f, 48);
        tcs.Update(recovered);
        Assert.False(tcs.PowerSampleAffectedByTcs);
        recorder.Record(recovered);

        Assert.Equal([2000f, 2300f], recorder.RawPowerSamples.Select(sample => sample.Rpm));
    }

    [Fact]
    public void RepeatedPowerCurvePassesRemoveTheKnownNarrowHighRpmTrough()
    {
        var settings = Settings();
        settings.BinCount = 256;
        settings.MinimumSamplesPerBin = 3;
        settings.MinimumIndependentPassesPerBin = 2;
        settings.SmoothingWindowRpm = 350f;
        settings.PowerCurveDipFraction = 0.03f;
        settings.SampleResidualThreshold = 0.05f;
        settings.MaximumNarrowValleyRpmSpan = 400f;
        settings.MinimumValleyRecoveryFraction = 0.95f;

        var recorder = new CalibrationRecorder(settings);
        recorder.Start(initialSnapshot: null);
        var points = new[]
        {
            (8021.676f, 810510.94f),
            (8055.66f, 808782.7f),
            (8089.6445f, 739424.6f),
            (8123.629f, 711930.9f),
            (8157.6133f, 794137.5f),
            (8191.5977f, 836873.4f),
            (8225.582f, 855412.75f),
        };

        for (var pass = 0; pass < 3; pass++)
        {
            recorder.ResetTemporalState();
            foreach (var (rpm, power) in points)
            {
                recorder.Record(Snapshot(rpm, power));
            }
        }

        Assert.True(recorder.TryGetPowerCurve(out var curve));
        Assert.DoesNotContain(recorder.RawPowerSamples, sample => sample.Rpm is >= 8080f and <= 8130f);
        Assert.DoesNotContain(curve, point => point.Rpm is >= 8080f and <= 8130f && point.Power < 780000f);
    }

    [Fact]
    public void CalibrationPersistsRawSamplesAlongsideTheCleanedCurve()
    {
        var path = Path.Combine(Path.GetTempPath(), $"forzahud-power-curve-{Guid.NewGuid():N}.json");
        try
        {
            var identity = new VehicleIdentity(10, 800, 2, 4, 8000f);
            var sample = new PowerCurveSample(
                4000f,
                300f,
                1f,
                2,
                6250f,
                0.02f,
                TimeSpan.FromMilliseconds(16));
            var store = new CalibrationDataStore(path);

            Assert.True(store.TrySave(new VehicleCalibration(
                identity,
                [new PowerCurvePoint(4000f, 300f)],
                new Dictionary<int, List<float>>())
            {
                RawPowerSamples = [sample],
            }));

            var saved = Assert.Single(store.FindExact(identity));
            Assert.Equal(sample, Assert.Single(saved.RawPowerSamples));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static PowerbandSettings Settings() => new()
    {
        MinimumThrottle = 0.95f,
        MinimumSpeed = 0f,
        MaximumRpmRate = 20000f,
        MaximumRawSamples = 1000,
        MaximumSamplesPerBin = 100,
    };

    private static TelemetrySnapshot Snapshot(
        float rpm,
        float power,
        float slip = 0f,
        int milliseconds = 0) =>
        TelemetrySnapshot.Empty with
        {
            IsRaceOn = true,
            EngineIdleRpm = 1000f,
            EngineMaxRpm = 9000f,
            CurrentEngineRpm = rpm,
            Speed = 20f,
            Power = power,
            Throttle = 1f,
            Gear = 2,
            DrivetrainType = 2,
            NumCylinders = 4,
            TireSlipRatioRearLeft = slip,
            TireSlipRatioRearRight = slip,
            ReceivedAt = TimeSpan.FromMilliseconds(milliseconds),
        };
}
