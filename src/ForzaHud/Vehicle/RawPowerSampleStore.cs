using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHud.Vehicle;

/// <summary>Stores raw power observations independently from the calibration index.</summary>
public sealed class RawPowerSampleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    static RawPowerSampleStore()
    {
        JsonOptions.Converters.Add(new RawPowerSamplesJsonConverter());
    }

    public RawPowerSampleStore(string directoryPath)
    {
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }

    public string GetFilePath(VehicleIdentity identity) =>
        Path.Combine(DirectoryPath, FileName(identity));

    public bool Exists(VehicleIdentity identity) => File.Exists(GetFilePath(identity));

    public bool TryLoad(VehicleIdentity identity, out List<PowerCurveSample> samples)
    {
        var path = GetFilePath(identity);
        if (!File.Exists(path))
        {
            samples = [];
            return false;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            samples = JsonSerializer.Deserialize<List<PowerCurveSample>>(gzip, JsonOptions) ?? [];
            return true;
        }
        catch (JsonException)
        {
            samples = [];
            return false;
        }
        catch (InvalidDataException)
        {
            samples = [];
            return false;
        }
        catch (IOException)
        {
            samples = [];
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            samples = [];
            return false;
        }
    }

    public bool TrySave(VehicleIdentity identity, IReadOnlyList<PowerCurveSample> samples)
    {
        var path = GetFilePath(identity);
        var temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            using (var file = File.Create(temporaryPath))
            using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            {
                JsonSerializer.Serialize(gzip, samples.ToList(), JsonOptions);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            TryDelete(temporaryPath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    public bool TryDelete(VehicleIdentity identity) => TryDelete(GetFilePath(identity));

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FileName(VehicleIdentity identity) =>
        string.Join(
            '-',
            "car",
            identity.CarOrdinal.ToString(CultureInfo.InvariantCulture),
            identity.CarPerformanceIndex.ToString(CultureInfo.InvariantCulture),
            identity.DrivetrainType.ToString(CultureInfo.InvariantCulture),
            identity.NumCylinders.ToString(CultureInfo.InvariantCulture),
            MathF.Round(identity.MaxRpm, MidpointRounding.AwayFromZero)
                .ToString("0", CultureInfo.InvariantCulture))
        + ".json.gz";
}

/// <summary>Writes each raw sample as a positional numeric array to reduce storage overhead.</summary>
public sealed class RawPowerSamplesJsonConverter : JsonConverter<List<PowerCurveSample>>
{
    public override List<PowerCurveSample>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var samples = new List<PowerCurveSample>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 10)
            {
                continue;
            }

            samples.Add(new PowerCurveSample(
                value[0].GetSingle(),
                value[1].GetSingle(),
                value[2].GetSingle(),
                value[3].GetInt32(),
                value[4].GetSingle(),
                value[5].GetSingle(),
                new TimeSpan(value[6].GetInt64()))
            {
                SpeedMetersPerSecond = value[7].GetSingle(),
                LongitudinalAcceleration = value[8].GetSingle(),
                DrivetrainType = value[9].GetInt32(),
            });
        }

        return samples;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<PowerCurveSample> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var sample in value)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(sample.Rpm);
            writer.WriteNumberValue(sample.Power);
            writer.WriteNumberValue(sample.Throttle);
            writer.WriteNumberValue(sample.Gear);
            writer.WriteNumberValue(sample.RpmRate);
            writer.WriteNumberValue(sample.WheelSlip);
            writer.WriteNumberValue(sample.Timestamp.Ticks);
            writer.WriteNumberValue(sample.SpeedMetersPerSecond);
            writer.WriteNumberValue(sample.LongitudinalAcceleration);
            writer.WriteNumberValue(sample.DrivetrainType);
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
