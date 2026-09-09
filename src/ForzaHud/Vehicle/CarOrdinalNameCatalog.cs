using System.Globalization;
using System.Text.Json;

namespace ForzaHud.Vehicle;

/// <summary>Resolves FH6 car ordinals to the human-facing names in the bundled catalog.</summary>
internal sealed class CarOrdinalNameCatalog
{
    public const string DefaultFileName = "Forza Horizon 6 Car Ordinals.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyDictionary<int, string> _names;

    private CarOrdinalNameCatalog(IReadOnlyDictionary<int, string> names)
    {
        _names = names;
    }

    public static CarOrdinalNameCatalog Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return Empty;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var nameToOrdinal = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOptions);
            if (nameToOrdinal is null)
            {
                return Empty;
            }

            var ordinalToName = new Dictionary<int, string>();
            foreach (var (name, ordinalText) in nameToOrdinal)
            {
                if (int.TryParse(ordinalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                {
                    ordinalToName[ordinal] = name;
                }
            }

            return new CarOrdinalNameCatalog(ordinalToName);
        }
        catch (JsonException)
        {
            return Empty;
        }
        catch (IOException)
        {
            return Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public string Resolve(int carOrdinal) =>
        _names.TryGetValue(carOrdinal, out var name)
            ? name
            : $"Unknown car (ordinal {carOrdinal.ToString(CultureInfo.InvariantCulture)})";

    private static CarOrdinalNameCatalog Empty { get; } =
        new(new Dictionary<int, string>());
}
