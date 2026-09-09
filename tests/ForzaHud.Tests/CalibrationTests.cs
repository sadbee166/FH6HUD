using ForzaHud.Configuration;
using System.Text.Json;
using ForzaHud.Telemetry;
using ForzaHud.Vehicle;
using Xunit;

namespace ForzaHud.Tests;

public sealed class CalibrationTests
{
    [Fact]
    public void DataStoreKeepsOneRecordPerPreciseIdentity()
    {
        var path = TemporaryPath();
        try
        {
            var store = new CalibrationDataStore(path);
            var identity = new VehicleIdentity(10, 800, 2, 6, 8000f);

            Assert.True(store.TrySave(Calibration(identity, 4000f)));
            Assert.True(store.TrySave(Calibration(identity, 5000f)));

            var saved = Assert.Single(store.FindExact(identity));
            Assert.Contains(saved.PowerCurve, point => point.Rpm == 5000f);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void DataStorePersistsTheProvidedCurveWithoutPostSaveCleaning()
    {
        var path = TemporaryPath();
        try
        {
            var store = new CalibrationDataStore(path);
            var identity = new VehicleIdentity(10, 800, 2, 6, 8500f);
            var curve = new List<PowerCurvePoint>
            {
                new(8021.676f, 810510.94f),
                new(8055.66f, 808782.7f),
                new(8089.6445f, 739424.6f),
                new(8123.629f, 711930.9f),
                new(8157.6133f, 794137.5f),
                new(8191.5977f, 836873.4f),
                new(8225.582f, 855412.75f),
            };

            Assert.True(store.TrySave(new VehicleCalibration(
                identity,
                curve,
                new Dictionary<int, List<float>>())));

            var saved = Assert.Single(store.FindExact(identity));

            Assert.Equal(curve, saved.PowerCurve);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void RawSamplesAreStoredOutsideTheCalibrationIndexInCompactForm()
    {
        var path = TemporaryPath();
        CalibrationDataStore? store = null;
        try
        {
            store = new CalibrationDataStore(path);
            var identity = new VehicleIdentity(10, 800, 2, 6, 8000f);
            var samples = Enumerable.Range(0, 128)
                .Select(index => new PowerCurveSample(
                    2000f + index,
                    100f + index,
                    1f,
                    2,
                    1000f,
                    0.02f,
                    TimeSpan.FromMilliseconds(index))
                {
                    SpeedMetersPerSecond = 20f + index,
                    LongitudinalAcceleration = 4f,
                    DrivetrainType = 2,
                })
                .ToList();

            Assert.True(store.TrySave(new VehicleCalibration(
                identity,
                [new PowerCurvePoint(4000f, 300f)],
                [])
            {
                RawPowerSamples = samples,
            }));

            var indexJson = File.ReadAllText(path);
            Assert.DoesNotContain("RawPowerSamples", indexJson);
            var rawFile = Assert.Single(Directory.GetFiles(store.RawSamplesDirectory, "*.json.gz"));
            Assert.True(new FileInfo(rawFile).Length < JsonSerializer.Serialize(samples).Length);
            Assert.Equal(samples, Assert.Single(store.FindExact(identity)).RawPowerSamples);
        }
        finally
        {
            DeleteIfPresent(path);
            if (store is not null && Directory.Exists(store.RawSamplesDirectory))
            {
                Directory.Delete(store.RawSamplesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void DataStoreReadsScalarRatioAndWritesOneAverageValue()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, """
            [
              {
                "Identity": {
                  "CarOrdinal": 10,
                  "CarPerformanceIndex": 800,
                  "DrivetrainType": 2,
                  "NumCylinders": 6,
                  "MaxRpm": 8000
                },
                "PowerCurve": [[2000, 100], [6000, 200]],
                "ShiftUpRpmDropRatioByGear": { "1": 0.5 }
              }
            ]
            """);

            var store = new CalibrationDataStore(path);
            var identity = new VehicleIdentity(10, 800, 2, 6, 8000f);
            var saved = Assert.Single(store.FindExact(identity));

            Assert.Equal([0.5f], saved.ShiftUpRpmDropRatioByGear[1]);

            saved.ShiftUpRpmDropRatioByGear[1].Add(0.6f);
            Assert.True(store.TrySave(saved));
            var json = File.ReadAllText(path);
            Assert.Contains("\"ShiftUpRpmDropRatioByGear\"", json);
            Assert.Contains("\"1\": 0.55", json);
            Assert.DoesNotContain("0.6", json);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void ProcessorLoadsExactCalibrationWithoutPowerCurveMatching()
    {
        var path = TemporaryPath();
        try
        {
            var identity = new VehicleIdentity(10, 800, 2, 6, 8000f);
            var store = new CalibrationDataStore(path);
            Assert.True(store.TrySave(new VehicleCalibration(
                identity,
                [
                    new PowerCurvePoint(2000f, 100f),
                    new PowerCurvePoint(4000f, 300f),
                    new PowerCurvePoint(6000f, 100f),
                ],
                new Dictionary<int, List<float>> { [1] = [0.5f] })));

            var processor = new VehicleStateProcessor(new HudConfiguration(), store);
            DerivedState? state = null;
            foreach (var (rpm, power) in new[] { (2000f, 900f), (4000f, 10f), (6000f, 900f) })
            {
                state = processor.Process(Snapshot(identity) with
                {
                    CurrentEngineRpm = rpm,
                    Power = power,
                });
            }

            Assert.NotNull(state);
            Assert.True(state.Powerband.IsLearned);
            Assert.Equal(4000f, state.Powerband.PeakPowerRpm);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void DataStoreMigratesLegacyInlineRawPowerSamplesToSidecarOnSave()
    {
        var path = TemporaryPath();
        CalibrationDataStore? store = null;
        try
        {
            File.WriteAllText(path, """
            [
              {
                "Identity": {
                  "CarOrdinal": 10,
                  "CarPerformanceIndex": 800,
                  "DrivetrainType": 2,
                  "NumCylinders": 6,
                  "MaxRpm": 8000
                },
                "CarName": "Test Vehicle",
                "PowerCurve": [[2000, 100], [6000, 200]],
                "ShiftUpRpmDropRatioByGear": { "1": 0.5 },
                "ShiftDurationMilliseconds": [150.0],
                "RawPowerSamples": [
                  {
                    "Rpm": 3000,
                    "Power": 150,
                    "Throttle": 1.0,
                    "Gear": 2,
                    "EngineIdleRpm": 1000,
                    "RpmRate": 500,
                    "Timestamp": "00:00:01",
                    "SpeedMetersPerSecond": 25.0,
                    "LongitudinalAcceleration": 3.5,
                    "DrivetrainType": 2
                  }
                ]
              }
            ]
            """);

            store = new CalibrationDataStore(path);
            var identity = new VehicleIdentity(10, 800, 2, 6, 8000f);
            var loaded = Assert.Single(store.FindExact(identity));

            Assert.Equal("Test Vehicle", loaded.CarName);
            Assert.Equal(2, loaded.PowerCurve.Count);
            Assert.Equal([0.5f], loaded.ShiftUpRpmDropRatioByGear[1]);
            Assert.Equal([150f], loaded.ShiftDurationMilliseconds);
            var originalSample = Assert.Single(loaded.RawPowerSamples);
            Assert.Equal(3000f, originalSample.Rpm);

            // Re-saving should migrate samples into a .json.gz sidecar and purge them from the index
            Assert.True(store.TrySave(loaded));

            var indexJson = File.ReadAllText(path);
            Assert.DoesNotContain("RawPowerSamples", indexJson);
            var rawFile = Assert.Single(Directory.GetFiles(store.RawSamplesDirectory, "*.json.gz"));
            Assert.True(new FileInfo(rawFile).Length > 0);

            // Verify a fresh data store instance reloads all migrated data from index + sidecar
            var freshStore = new CalibrationDataStore(path);
            var reloaded = Assert.Single(freshStore.FindExact(identity));
            Assert.Equal(loaded.Identity, reloaded.Identity);
            Assert.Equal(loaded.CarName, reloaded.CarName);
            Assert.Equal(loaded.PowerCurve, reloaded.PowerCurve);
            Assert.Equal(loaded.ShiftUpRpmDropRatioByGear, reloaded.ShiftUpRpmDropRatioByGear);
            Assert.Equal(loaded.ShiftDurationMilliseconds, reloaded.ShiftDurationMilliseconds);
            Assert.Equal(loaded.RawPowerSamples, reloaded.RawPowerSamples);
        }
        finally
        {
            DeleteIfPresent(path);
            if (store is not null && Directory.Exists(store.RawSamplesDirectory))
            {
                Directory.Delete(store.RawSamplesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void SidecarIsPreservedWhenIndexWriteFails()
    {
        var path = TemporaryPath();
        CalibrationDataStore? store = null;
        try
        {
            store = new CalibrationDataStore(path);
            var identity = new VehicleIdentity(10, 800, 2, 6, 8000f);
            var samples = new List<PowerCurveSample>
            {
                new(3000f, 150f, 1f, 2, 1000f, 0.02f, TimeSpan.FromSeconds(1)),
            };

            Assert.True(store.TrySave(new VehicleCalibration(
                identity,
                [new PowerCurvePoint(4000f, 300f)],
                [])
            {
                RawPowerSamples = samples,
            }));

            var sidecarFiles = Directory.GetFiles(store.RawSamplesDirectory, "*.json.gz");
            var sidecarFile = Assert.Single(sidecarFiles);
            Assert.True(File.Exists(sidecarFile));

            // Block index write by creating a directory where the temporary file needs to be written
            var tempFile = path + ".tmp";
            Directory.CreateDirectory(tempFile);

            try
            {
                // Updating with more samples will fail to write index
                var updatedSamples = new List<PowerCurveSample>(samples)
                {
                    new(3500f, 180f, 1f, 2, 1000f, 0.02f, TimeSpan.FromSeconds(2)),
                };
                var failedSave = store.TrySave(new VehicleCalibration(
                    identity,
                    [new PowerCurvePoint(4000f, 300f)],
                    [])
                {
                    RawPowerSamples = updatedSamples,
                });

                Assert.False(failedSave);
                // The sidecar file must not be removed or deleted on index write failure
                Assert.True(File.Exists(sidecarFile));
            }
            finally
            {
                if (Directory.Exists(tempFile))
                {
                    Directory.Delete(tempFile);
                }
            }
        }
        finally
        {
            DeleteIfPresent(path);
            if (store is not null && Directory.Exists(store.RawSamplesDirectory))
            {
                Directory.Delete(store.RawSamplesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void ShapeErrorUsesOnlyTheOverlappingRpmRange()
    {
        Assert.True(PowerCurveShapeComparer.TryGetShapeError(
            [new PowerCurvePoint(2000f, 100f), new PowerCurvePoint(4000f, 200f), new PowerCurvePoint(6000f, 100f)],
            [new PowerCurvePoint(3000f, 200f), new PowerCurvePoint(5000f, 200f)],
            out var error));

        Assert.Equal(0f, error);
    }

    private static VehicleCalibration Calibration(VehicleIdentity identity, float peakRpm) =>
        new(
            identity,
            [
                new PowerCurvePoint(2500f, 100f),
                new PowerCurvePoint(peakRpm, 300f),
                new PowerCurvePoint(6500f, 100f),
            ],
            new Dictionary<int, List<float>> { [1] = [0.5f] });

    private static TelemetrySnapshot Snapshot(VehicleIdentity identity) => TelemetrySnapshot.Empty with
    {
        IsRaceOn = true,
        EngineIdleRpm = 1000f,
        EngineMaxRpm = identity.MaxRpm,
        CurrentEngineRpm = 3000f,
        Speed = 20f,
        Power = 100f,
        Throttle = 1f,
        Gear = 1,
        CarOrdinal = identity.CarOrdinal,
        CarPerformanceIndex = identity.CarPerformanceIndex,
        DrivetrainType = identity.DrivetrainType,
        NumCylinders = identity.NumCylinders,
    };

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"forzahud-calibration-{Guid.NewGuid():N}.json");

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
