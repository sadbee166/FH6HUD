using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHud.Vehicle;

/// <summary>Stores power-curve points as compact <c>[rpm, power]</c> pairs.</summary>
public sealed class PowerCurveJsonConverter : JsonConverter<List<PowerCurvePoint>>
{
    private static readonly JsonSerializerOptions CompactOptions = new();

    public override List<PowerCurvePoint>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var curve = new List<PowerCurvePoint>();
        foreach (var point in document.RootElement.EnumerateArray())
        {
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
            {
                curve.Add(new PowerCurvePoint(
                    point[0].GetSingle(),
                    point[1].GetSingle()));
            }
            else if (point.ValueKind == JsonValueKind.Object
                     && point.TryGetProperty("Rpm", out var rpm)
                     && point.TryGetProperty("Power", out var power))
            {
                curve.Add(new PowerCurvePoint(rpm.GetSingle(), power.GetSingle()));
            }
        }

        return curve;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<PowerCurvePoint> value,
        JsonSerializerOptions options)
    {
        var compactCurve = value
            .Select(point => new[] { point.Rpm, point.Power })
            .ToArray();
        writer.WriteRawValue(JsonSerializer.Serialize(compactCurve, CompactOptions));
    }
}
