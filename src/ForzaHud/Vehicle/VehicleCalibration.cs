using System.Text.Json;
using System.Text.Json.Serialization;
using ForzaHud.Configuration;
using ForzaHud.Telemetry;

namespace ForzaHud.Vehicle;

/// <summary>Precise identity of one telemetry vehicle configuration.</summary>
public readonly record struct VehicleIdentity(
    int CarOrdinal,
    int CarPerformanceIndex,
    int DrivetrainType,
    int NumCylinders,
    float MaxRpm)
{
    public bool IsElectric => NumCylinders == 0;


    public static VehicleIdentity From(TelemetrySnapshot snapshot) => new(
        snapshot.CarOrdinal,
        snapshot.CarPerformanceIndex,
        snapshot.DrivetrainType,
        snapshot.NumCylinders,
        snapshot.EngineMaxRpm);
}

/// <summary>One measured point in a car's power curve.</summary>
public readonly record struct PowerCurvePoint(float Rpm, float Power);

/// <summary>Measured power-curve coverage used by live calibration stop gates.</summary>
public sealed record CalibrationAnalysis(
    int AcceptedPowerSamples,
    int PopulatedRpmBins,
    float RpmCoverageFraction)
{
    /// <summary>Samples removed by the in-memory shape/refit layer.</summary>
    public int RejectedPowerSamples { get; init; }

    /// <summary>Populated bins whose retained observations meet the reliability gates.</summary>
    public int ReliableRpmBins { get; init; }
}

/// <summary>One persisted calibration record.</summary>
public sealed record VehicleCalibration(
    VehicleIdentity Identity,
    [property: JsonConverter(typeof(PowerCurveJsonConverter))]
    List<PowerCurvePoint> PowerCurve,
    [property: JsonConverter(typeof(ShiftRatioJsonConverter))]
    Dictionary<int, List<float>> ShiftUpRpmDropRatioByGear)
{
    /// <summary>Raw observations retained in the independent raw-sample store.</summary>
    [JsonIgnore]
    public List<PowerCurveSample> RawPowerSamples { get; init; } = [];

    /// <summary>Measured upshift durations in milliseconds, shared across all gears.</summary>
    public List<float> ShiftDurationMilliseconds { get; init; } = [];

    /// <summary>Human-facing car name resolved from the FH6 car ordinal catalog when saved.</summary>
    [JsonPropertyOrder(-1)]
    public string? CarName { get; init; }

    /// <summary>Whether this record explicitly disables RPM calibration for the vehicle.</summary>
    public bool IsRpmCalibrationDisabled =>
        PowerCurve.Count == 1
        && PowerCurve[0] == new PowerCurvePoint(-1f, -1f);



}

internal sealed class ShiftRatioJsonConverter : JsonConverter<Dictionary<int, List<float>>>
{
    public override Dictionary<int, List<float>> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = new Dictionary<int, List<float>>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var gear))
            {
                continue;
            }

            var samples = property.Value.ValueKind switch
            {
                JsonValueKind.Array => property.Value.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.Number)
                    .Select(value => value.GetSingle())
                    .ToList(),
                JsonValueKind.Number => [property.Value.GetSingle()],
                _ => [],
            };

            var validSamples = samples
                .Where(float.IsFinite)
                .Where(sample => sample > 0f && sample < 1f)
                .ToArray();
            if (validSamples.Length > 0)
            {
                result[gear] = [validSamples.Average()];
            }
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<int, List<float>> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (gear, samples) in value.OrderBy(pair => pair.Key))
        {
            var validSamples = samples
                .Where(float.IsFinite)
                .Where(sample => sample > 0f && sample < 1f)
                .ToArray();
            if (validSamples.Length == 0)
            {
                continue;
            }

            writer.WritePropertyName(gear.ToString());
            writer.WriteNumberValue(validSamples.Average());
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Reads and updates the JSON collection of calibrations. A save keeps one record per precise
/// identity.
/// </summary>
public sealed class CalibrationDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public CalibrationDataStore(
        string filePath,
        string? carOrdinalNamesFilePath = null,
        string? rawSamplesDirectory = null)
    {
        FilePath = Path.GetFullPath(filePath);
        var namesPath = string.IsNullOrWhiteSpace(carOrdinalNamesFilePath)
            ? Path.Combine(AppContext.BaseDirectory, CarOrdinalNameCatalog.DefaultFileName)
            : Path.GetFullPath(carOrdinalNamesFilePath);
        _carNames = CarOrdinalNameCatalog.Load(namesPath);
        RawSamplesDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(rawSamplesDirectory)
                ? Path.Combine(
                    Path.GetDirectoryName(FilePath)!,
                    Path.GetFileNameWithoutExtension(FilePath) + "-raw")
                : rawSamplesDirectory);
        _rawSamples = new RawPowerSampleStore(RawSamplesDirectory);
    }

    public string FilePath { get; }

    public string RawSamplesDirectory { get; }

    private readonly CarOrdinalNameCatalog _carNames;
    private readonly RawPowerSampleStore _rawSamples;

    public IReadOnlyList<VehicleCalibration> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            var fileRecords = JsonSerializer.Deserialize<List<CalibrationFileRecord>>(stream, JsonOptions) ?? [];
            var records = fileRecords.Select(record =>
            {
                var rawSamples = _rawSamples.TryLoad(record.Identity, out var storedSamples)
                    ? storedSamples
                    : record.RawPowerSamples ?? [];
                return record.ToCalibration(rawSamples);
            });
            return Normalize(records);
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<VehicleCalibration> FindExact(VehicleIdentity identity) =>
        Load().Where(record => IsSameVehicleIdentity(record.Identity, identity)).ToArray();

    /// <summary>Saves a calibration, replacing the record for the same precise identity.</summary>
    public bool TrySave(VehicleCalibration calibration)
    {
        var calibrationToSave = calibration;
        var records = Load().Select(WithCarName).ToList();
        var existing = records.FirstOrDefault(record => IsSameVehicleIdentity(record.Identity, calibrationToSave.Identity));
        if (calibrationToSave.RawPowerSamples.Count == 0 && existing?.RawPowerSamples.Count > 0)
        {
            calibrationToSave = calibrationToSave with
            {
                RawPowerSamples = existing.RawPowerSamples.ToList(),
            };
        }

        records.RemoveAll(record => IsSameVehicleIdentity(record.Identity, calibrationToSave.Identity));
        var stored = WithCarName(calibrationToSave with { CarName = calibrationToSave.CarName ?? existing?.CarName });
        records.Add(stored);

        var rawSamplesChanged = existing is null
            ? stored.RawPowerSamples.Count > 0 || _rawSamples.Exists(stored.Identity)
            : !existing.RawPowerSamples.SequenceEqual(stored.RawPowerSamples)
              || (stored.RawPowerSamples.Count > 0 && !_rawSamples.Exists(stored.Identity));
        if (rawSamplesChanged)
        {
            var rawSamplesSaved = stored.RawPowerSamples.Count == 0
                ? _rawSamples.TryDelete(stored.Identity)
                : _rawSamples.TrySave(stored.Identity, stored.RawPowerSamples);
            if (!rawSamplesSaved)
            {
                return false;
            }
        }

        return TryWrite(records);
    }

    /// <summary>Deletes every calibration record sharing the normalized vehicle identity.</summary>
    public bool TryDelete(VehicleIdentity identity)
    {
        var records = Load().Select(WithCarName).ToList();
        if (records.RemoveAll(record => IsSameVehicleIdentity(record.Identity, identity)) == 0)
        {
            return false;
        }

        if (!TryWrite(records))
        {
            return false;
        }

        _rawSamples.TryDelete(identity);
        return true;
    }

    private bool TryWrite(IReadOnlyList<VehicleCalibration> records)
    {
        var temporaryPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var fileRecords = records.Select(CalibrationFileRecord.FromCalibration).ToList();
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(fileRecords, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            return false;
        }
    }

    private sealed class CalibrationFileRecord
    {
        public VehicleIdentity Identity { get; set; }

        [JsonConverter(typeof(PowerCurveJsonConverter))]
        public List<PowerCurvePoint> PowerCurve { get; set; } = [];

        [JsonConverter(typeof(ShiftRatioJsonConverter))]
        public Dictionary<int, List<float>> ShiftUpRpmDropRatioByGear { get; set; } = [];

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PowerCurveSample>? RawPowerSamples { get; set; }

        public List<float> ShiftDurationMilliseconds { get; set; } = [];

        public string? CarName { get; set; }

        public VehicleCalibration ToCalibration(IReadOnlyList<PowerCurveSample> rawPowerSamples) =>
            new(Identity, PowerCurve, ShiftUpRpmDropRatioByGear)
            {
                RawPowerSamples = rawPowerSamples.ToList(),
                ShiftDurationMilliseconds = ShiftDurationMilliseconds,
                CarName = CarName,
            };

        public static CalibrationFileRecord FromCalibration(VehicleCalibration calibration) => new()
        {
            Identity = calibration.Identity,
            PowerCurve = calibration.PowerCurve,
            ShiftUpRpmDropRatioByGear = calibration.ShiftUpRpmDropRatioByGear,
            ShiftDurationMilliseconds = calibration.ShiftDurationMilliseconds,
            CarName = calibration.CarName,
        };
    }

    private static List<VehicleCalibration> Normalize(IEnumerable<VehicleCalibration> records)
    {
        var normalized = new List<VehicleCalibration>();
        foreach (var record in records)
        {
            var candidate = record with
            {
                Identity = NormalizeIdentity(record.Identity),
                PowerCurve = record.PowerCurve ?? [],
                RawPowerSamples = NormalizePowerSamples(record.RawPowerSamples),
                ShiftUpRpmDropRatioByGear = NormalizeShiftSamples(record.ShiftUpRpmDropRatioByGear),
                ShiftDurationMilliseconds = NormalizeShiftDurations(record.ShiftDurationMilliseconds),
            };
            var existingIndex = normalized.FindIndex(existing => IsSameVehicleIdentity(existing.Identity, candidate.Identity));
            if (existingIndex >= 0)
            {
                normalized[existingIndex] = candidate;
            }
            else
            {
                normalized.Add(candidate);
            }
        }

        return normalized;
    }

    private VehicleCalibration WithCarName(VehicleCalibration calibration) =>
        calibration with { CarName = calibration.CarName ?? _carNames.Resolve(calibration.Identity.CarOrdinal) };

    private static bool IsSameVehicleIdentity(VehicleIdentity left, VehicleIdentity right) =>
        NormalizeIdentity(left) == NormalizeIdentity(right);

    private static Dictionary<int, List<float>> NormalizeShiftSamples(
        IReadOnlyDictionary<int, List<float>>? samples)
    {
        var normalized = new Dictionary<int, List<float>>();
        if (samples is null)
        {
            return normalized;
        }

        foreach (var (gear, values) in samples)
        {
            var validValues = values?
                .Where(float.IsFinite)
                .Where(value => value > 0f && value < 1f)
                .ToArray() ?? [];
            if (validValues.Length > 0)
            {
                normalized[gear] = [validValues.Average()];
            }
        }

        return normalized;
    }

    private static List<float> NormalizeShiftDurations(IReadOnlyList<float>? samples) =>
        samples?
            .Where(float.IsFinite)
            .Where(value => value > 0f)
            .ToList()
        ?? [];

    private static List<PowerCurveSample> NormalizePowerSamples(
        IReadOnlyList<PowerCurveSample>? samples) =>
        samples?
            .Where(sample => float.IsFinite(sample.Rpm)
                             && float.IsFinite(sample.Power)
                             && float.IsFinite(sample.Throttle)
                             && float.IsFinite(sample.RpmRate)
                             && float.IsFinite(sample.WheelSlip))
            .Where(sample => sample.Rpm > 0f
                             && sample.Power > 0f
                             && sample.Throttle is >= 0f and <= 1f
                             && sample.Gear is >= 1 and <= 10
                             && sample.WheelSlip >= 0f)
            .ToList()
        ?? [];

    private static VehicleIdentity NormalizeIdentity(VehicleIdentity identity) =>
        identity with { MaxRpm = MathF.Round(identity.MaxRpm, MidpointRounding.AwayFromZero) };

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Compares two curves only when deciding whether a live curve should replace one.</summary>
internal static class PowerCurveShapeComparer
{
    public static bool TryGetShapeError(
        IReadOnlyList<PowerCurvePoint> stored,
        IReadOnlyList<PowerCurvePoint> live,
        out float error)
    {
        var storedCurve = ValidCurve(stored);
        var liveCurve = ValidCurve(live);
        error = float.PositiveInfinity;
        if (storedCurve.Length == 0 || liveCurve.Length == 0)
        {
            return false;
        }

        var overlapStart = Math.Max(storedCurve[0].Rpm, liveCurve[0].Rpm);
        var overlapEnd = Math.Min(storedCurve[^1].Rpm, liveCurve[^1].Rpm);
        if (overlapEnd < overlapStart)
        {
            return false;
        }

        var storedOverlap = storedCurve.Where(point => point.Rpm >= overlapStart && point.Rpm <= overlapEnd).ToArray();
        var liveOverlap = liveCurve.Where(point => point.Rpm >= overlapStart && point.Rpm <= overlapEnd).ToArray();
        if (storedOverlap.Length == 0 || liveOverlap.Length == 0)
        {
            return false;
        }

        var storedPeak = storedOverlap.Max(point => point.Power);
        var livePeak = liveOverlap.Max(point => point.Power);
        var differences = new List<float>(storedOverlap.Length + liveOverlap.Length);
        AddShapeDifferences(storedOverlap, liveOverlap, storedPeak, livePeak, differences);
        AddShapeDifferences(liveOverlap, storedOverlap, livePeak, storedPeak, differences);
        error = differences.Average();
        return true;
    }

    private static PowerCurvePoint[] ValidCurve(IReadOnlyList<PowerCurvePoint> curve) =>
        curve.Where(point => float.IsFinite(point.Rpm) && float.IsFinite(point.Power)
                             && point.Rpm > 0f && point.Power > 0f)
            .OrderBy(point => point.Rpm)
            .ToArray();

    private static void AddShapeDifferences(
        IReadOnlyList<PowerCurvePoint> source,
        IReadOnlyList<PowerCurvePoint> target,
        float sourcePeak,
        float targetPeak,
        List<float> differences)
    {
        foreach (var point in source)
        {
            var nearest = target.MinBy(candidate => Math.Abs(candidate.Rpm - point.Rpm));
            differences.Add(Math.Abs(point.Power / sourcePeak - nearest.Power / targetPeak));
        }
    }
}

/// <summary>Fresh calibration run fed for one manual or live collection session.</summary>
public sealed class CalibrationRecorder
{
    private readonly PowerCurveRecorder _powerCurve;
    private readonly ShiftUpRpmDropRatioLogger _shiftLogger = new();
    private readonly IReadOnlyList<PowerCurveSample> _initialPowerSamples;
    private VehicleIdentity? _identity;
    private bool _invalidated;

    public CalibrationRecorder(
        PowerbandSettings settings,
        TractionControlSettings? tractionControl = null,
        IReadOnlyList<PowerCurveSample>? initialPowerSamples = null)
    {
        _initialPowerSamples = initialPowerSamples ?? [];
        _powerCurve = new PowerCurveRecorder(settings, tractionControl);
    }

    public bool IsRecording { get; private set; }

    /// <summary>Vehicle identity observed by this run, if usable telemetry has arrived.</summary>
    public VehicleIdentity? Identity => _identity;

    /// <summary>Whether telemetry from another vehicle invalidated this run.</summary>
    public bool WasInvalidated => _invalidated;

    /// <summary>Number of accepted wide-open-throttle power samples.</summary>
    public int AcceptedPowerSamples => _powerCurve.AcceptedPowerSamples;

    /// <summary>Number of samples rejected by the in-memory shape refit.</summary>
    public int RejectedPowerSamples => _powerCurve.RejectedPowerSamples;

    /// <summary>Raw samples remaining after the latest shape-cleaning pass.</summary>
    public IReadOnlyList<PowerCurveSample> RawPowerSamples => _powerCurve.RawPowerSamples;

    /// <summary>Confidence diagnostics for the currently populated RPM bins.</summary>
    public IReadOnlyList<PowerCurveBinConfidence> PowerCurveBinConfidence => _powerCurve.BinConfidence;

    /// <summary>Ends the temporal validity window without discarding retained samples.</summary>
    public void ResetTemporalState() => _powerCurve.ResetTemporalState();

    /// <summary>Returns the current measured calibration gates for terminal diagnostics.</summary>
    public CalibrationAnalysis GetCalibrationAnalysis() => new(
        _powerCurve.AcceptedPowerSamples,
        _powerCurve.PopulatedRpmBins,
        _powerCurve.RpmCoverageFraction)
    {
        RejectedPowerSamples = _powerCurve.RejectedPowerSamples,
        ReliableRpmBins = _powerCurve.BinConfidence.Count(
            bin => bin.Status == PowerCurveBinStatus.Reliable),
    };

    public bool TryGetPowerCurve(out List<PowerCurvePoint> powerCurve)
    {
        powerCurve = _powerCurve.CreatePowerCurve();
        if (powerCurve.Count > 0)
        {
            return true;
        }

        powerCurve = [];
        return false;
    }

    public void Start(
        TelemetrySnapshot? initialSnapshot,
        bool includeInitialPowerSample = true)
    {
        IsRecording = true;
        _identity = null;
        _invalidated = false;
        _powerCurve.Reset();
        _powerCurve.Seed(_initialPowerSamples);
        _shiftLogger.Reset();

        if (initialSnapshot is not null)
        {
            Record(initialSnapshot, includeInitialPowerSample);
        }
    }

    /// <param name="includePowerSample">Whether this frame may contribute to the power curve. Shift logging is always updated.</param>
    public ShiftUpRpmDropRatio? Record(TelemetrySnapshot snapshot, bool includePowerSample = true)
    {
        if (!IsRecording)
        {
            return null;
        }

        if (!IsUsableSnapshot(snapshot))
        {
            _powerCurve.ResetTemporalState();
            return null;
        }

        var identity = VehicleIdentity.From(snapshot);
        if (_identity is null)
        {
            _identity = identity;
        }
        else if (_identity.Value != identity)
        {
            _invalidated = true;
            return null;
        }

        if (_identity == identity && !_powerCurve.HasRpmRange)
        {
            _powerCurve.SetRpmRange(snapshot.EngineIdleRpm, snapshot.EngineMaxRpm);
        }

        _powerCurve.Record(snapshot, includePowerSample);
        return _shiftLogger.Observe(snapshot);
    }

    public bool TryFinish(out VehicleCalibration calibration)
    {
        IsRecording = false;
        if (_invalidated || _identity is null || _identity.Value.IsElectric
            || !TryGetPowerCurve(out var powerCurve))
        {
            calibration = default!;
            return false;
        }

        calibration = new VehicleCalibration(
            _identity.Value,
            powerCurve,
            _shiftLogger.Snapshot())
        {
            RawPowerSamples = RawPowerSamples.ToList(),
            ShiftDurationMilliseconds = _shiftLogger.DurationSnapshot(),
        };
        return true;
    }

    private static bool IsUsableSnapshot(TelemetrySnapshot snapshot) =>
        snapshot.IsRaceOn
        && snapshot.EngineMaxRpm > snapshot.EngineIdleRpm
        && snapshot.EngineMaxRpm > 0
        && snapshot.NumCylinders != 0;
}
