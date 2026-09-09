using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHud.Rendering;

/// <summary>A point in device-independent HUD space.</summary>
public readonly record struct HudPoint(float X, float Y)
{
    public static HudPoint operator +(HudPoint a, HudPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static HudPoint operator -(HudPoint a, HudPoint b) => new(a.X - b.X, a.Y - b.Y);

    public static HudPoint operator *(HudPoint a, float scale) => new(a.X * scale, a.Y * scale);
}

/// <summary>Affine transform used to move, scale, and rotate a complete HUD panel.</summary>
public readonly record struct HudTransform(float Scale, HudPoint Anchor, HudPoint Offset, float RotationDegrees = 0f);

/// <summary>
/// Projective transform for rotating a flat HUD panel around screen-space X and Y pivot lines.
/// Positive Z is toward the viewer; angles are in degrees.
/// </summary>
public readonly record struct HudDepthTransform(
    float YawDegrees,
    float PitchDegrees,
    HudPoint Pivot,
    float PerspectiveDistance)
{
    public bool IsIdentity => YawDegrees == 0f && PitchDegrees == 0f;

    /// <summary>Builds the Direct2D-compatible projective matrix for the flat panel.</summary>
    public Matrix4x4 ToPerspectiveMatrix()
    {
        var yaw = MathF.PI * YawDegrees / 180f;
        var pitch = MathF.PI * PitchDegrees / 180f;
        var sinYaw = MathF.Sin(yaw);
        var cosYaw = MathF.Cos(yaw);
        var sinPitch = MathF.Sin(pitch);
        var cosPitch = MathF.Cos(pitch);

        // Rotate the flat panel around its pivot, then project it from a finite distance.
        // The matrix uses row-vector notation, matching D2D_MATRIX_4X4_F.
        var depth = PerspectiveDistance;
        var weightX = -cosPitch * sinYaw / depth;
        var weightY = -sinPitch / depth;
        var weightConstant = 1f - weightX * Pivot.X - weightY * Pivot.Y;

        return new Matrix4x4(
            Pivot.X * weightX + cosYaw,
            Pivot.Y * weightX - sinPitch * sinYaw,
            0f,
            weightX,
            Pivot.X * weightY,
            Pivot.Y * weightY + cosPitch,
            0f,
            weightY,
            0f,
            0f,
            1f,
            0f,
            Pivot.X * weightConstant - cosYaw * Pivot.X,
            Pivot.Y * weightConstant - cosPitch * Pivot.Y + sinPitch * sinYaw * Pivot.X,
            0f,
            weightConstant);
    }

    /// <summary>Projects one screen-space point through this depth transform.</summary>
    public HudPoint Project(HudPoint point)
    {
        var transformed = Vector4.Transform(new Vector4(point.X, point.Y, 0f, 1f), ToPerspectiveMatrix());
        return new HudPoint(transformed.X / transformed.W, transformed.Y / transformed.W);
    }
}

/// <summary>Size of a measured piece of text or geometry.</summary>
public readonly record struct HudSize(float Width, float Height);

/// <summary>A rectangle in device-independent HUD space.</summary>
public readonly record struct HudRect(float X, float Y, float Width, float Height)
{
    public float Left => X;

    public float Top => Y;

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public HudPoint Center => new(X + Width / 2f, Y + Height / 2f);

    public static HudRect FromCenter(HudPoint center, HudSize size) =>
        new(center.X - size.Width / 2f, center.Y - size.Height / 2f, size.Width, size.Height);
}

/// <summary>An RGBA colour. Kept independent of the underlying renderer.</summary>
[JsonConverter(typeof(HudColorJsonConverter))]
public readonly record struct HudColor(
    [property: JsonPropertyName("r")] byte R = 0,
    [property: JsonPropertyName("g")] byte G = 0,
    [property: JsonPropertyName("b")] byte B = 0,
    [property: JsonPropertyName("a")] byte A = 255)
{
    /// <summary>Parses a "#RRGGBBAA", "#RGBA", "#RRGGBB" or "#RGB" hex string. Returns opaque black when unparseable.</summary>
    public static HudColor Parse(string? hex) => TryParse(hex, out var color)
        ? color
        : new HudColor(0, 0, 0, 255);

    /// <summary>Parses a "#RRGGBBAA", "#RGBA", "#RRGGBB" or "#RGB" hex string.</summary>
    public static bool TryParse(string? hex, out HudColor color)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            color = default;
            return false;
        }

        var text = hex.TrimStart('#');
        if (text.Length == 3)
        {
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);
        }
        else if (text.Length == 4)
        {
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2], text[3], text[3]);
        }

        if (text.Length == 6)
        {
            if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var red)
                || !byte.TryParse(text.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var green)
                || !byte.TryParse(text.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var blue))
            {
                color = default;
                return false;
            }

            color = new HudColor(red, green, blue, 255);
            return true;
        }

        if (text.Length == 8)
        {
            if (!byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var red)
                || !byte.TryParse(text.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var green)
                || !byte.TryParse(text.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var blue)
                || !byte.TryParse(text.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var alpha))
            {
                color = default;
                return false;
            }

            color = new HudColor(red, green, blue, alpha);
            return true;
        }

        color = default;
        return false;
    }
}

/// <summary>A colour plus the opacity to draw it with.</summary>
public readonly record struct HudPaint(HudColor Color, float Opacity = 1f)
{
    /// <summary>
    /// Alpha after the paint opacity (including the element's configured opacity) is applied.
    /// </summary>
    public byte EffectiveAlpha =>
        (byte)Math.Clamp(MathF.Round(Color.A * Math.Clamp(Opacity, 0f, 1f)), 0f, 255f);

    public HudPaint WithOpacity(float opacity) => new(Color, opacity);
}

/// <summary>Typographic role of a piece of text.</summary>
public enum HudTextStyle
{
    /// <summary>Small supporting label.</summary>
    Label,

    /// <summary>Secondary numeric readout.</summary>
    Value,

    /// <summary>Gear indicator: prominent, but subordinate to speed.</summary>
    Gear,

    /// <summary>Dominant readout: speed.</summary>
    Primary,
}

/// <summary>Horizontal anchoring of text around its draw position.</summary>
public enum HudTextAnchor
{
    Leading,
    Center,
    Trailing,
}

/// <summary>Vertical anchoring of text around its draw position.</summary>
public enum HudTextBaseline
{
    Top,
    Middle,
    Bottom,
}

/// <summary>Custom JSON converter for <see cref="HudColor"/> objects with RGBA components.</summary>
public sealed class HudColorJsonConverter : JsonConverter<HudColor>
{
    public override HudColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected JSON object for {nameof(HudColor)}, got {reader.TokenType}.");
        }

        byte r = 0;
        byte g = 0;
        byte b = 0;
        byte a = 255;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new HudColor(r, g, b, a);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected property name, got {reader.TokenType}.");
            }

            var propertyName = reader.GetString();
            reader.Read();

            if (string.Equals(propertyName, "r", StringComparison.OrdinalIgnoreCase))
            {
                r = reader.GetByte();
            }
            else if (string.Equals(propertyName, "g", StringComparison.OrdinalIgnoreCase))
            {
                g = reader.GetByte();
            }
            else if (string.Equals(propertyName, "b", StringComparison.OrdinalIgnoreCase))
            {
                b = reader.GetByte();
            }
            else if (string.Equals(propertyName, "a", StringComparison.OrdinalIgnoreCase))
            {
                a = reader.GetByte();
            }
            else
            {
                reader.Skip();
            }
        }

        throw new JsonException("Unexpected end of JSON while reading HudColor.");
    }

    public override void Write(Utf8JsonWriter writer, HudColor value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("r", value.R);
        writer.WriteNumber("g", value.G);
        writer.WriteNumber("b", value.B);
        writer.WriteNumber("a", value.A);
        writer.WriteEndObject();
    }
}
