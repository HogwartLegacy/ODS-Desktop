using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ods.Core.Catalog;

/// <summary>
/// Python value semantics for catalog data. The selector ports
/// (<see cref="ModelMemory"/>, <see cref="ModelSelection"/>) read
/// config/model-library.json through these helpers so that they coerce, test
/// truthiness and round exactly like model_memory.py and model_selection.py.
/// </summary>
internal static partial class Py
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonKeyCharacters();

    /// <summary>Python truthiness of a JSON value (None, False, 0, "", [] and {} are false).</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => NumberValue(value) != 0,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>A JSON number as a double, whether parsed or created from a CLR value.</summary>
    public static double NumberValue(JsonValue value)
    {
        if (value.TryGetValue(out double d)) return d;
        if (value.TryGetValue(out long l)) return l;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out decimal m)) return (double)m;
        if (value.TryGetValue(out float f)) return f;
        if (value.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        throw new InvalidOperationException($"JSON value {value.ToJsonString()} is not a number.");
    }

    /// <summary>Python <c>a or b</c>.</summary>
    public static JsonNode? Or(JsonNode? first, JsonNode? second) => Truthy(first) ? first : second;

    /// <summary>Python <c>float(value)</c>, or null where Python raises TypeError/ValueError.</summary>
    public static double? ToFloat(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return NumberValue(value);
            case JsonValueKind.True:
                return 1.0;
            case JsonValueKind.False:
                return 0.0;
            case JsonValueKind.String:
                return double.TryParse(value.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>model_memory._positive_number.</summary>
    public static double PositiveNumber(JsonNode? node)
    {
        var number = ToFloat(node);
        return number is double n && double.IsFinite(n) && n > 0 ? n : 0.0;
    }

    /// <summary>model_memory._non_negative_number (booleans are rejected).</summary>
    public static double? NonNegativeNumber(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return null;
        var number = ToFloat(node);
        return number is double n && double.IsFinite(n) && n >= 0 ? n : null;
    }

    /// <summary>Python <c>int(value)</c>, or null where Python raises.</summary>
    public static long? ToInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                var number = NumberValue(value);
                return double.IsFinite(number) ? (long)Math.Truncate(number) : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            case JsonValueKind.String:
                return long.TryParse(value.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Python <c>int(value or 0)</c> inside try/except returning <paramref name="fallback"/>.</summary>
    public static int IntOr(JsonNode? node, int fallback = 0)
    {
        if (!Truthy(node)) return 0;
        var number = ToInt(node);
        return number is long n ? (int)n : fallback;
    }

    /// <summary>Python <c>str(value or "")</c>.</summary>
    public static string Str(JsonNode? node)
    {
        if (!Truthy(node)) return "";
        if (node is JsonValue value)
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => value.ToJsonString(),
            };
        }
        return node!.ToJsonString();
    }

    /// <summary>Python <c>str(value)</c> (None becomes "None", 0 stays "0").</summary>
    public static string StrExact(JsonNode? node)
    {
        if (node is null) return "None";
        if (node is JsonValue value)
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => value.ToJsonString(),
            };
        }
        return node.ToJsonString();
    }

    /// <summary>model_selection.normalize_key.</summary>
    public static string NormalizeKey(string? value) =>
        NonKeyCharacters().Replace((value ?? "").ToLowerInvariant(), "-").Trim('-');

    public static string NormalizeKey(JsonNode? node) => NormalizeKey(Str(node));

    /// <summary>model_selection.list_value.</summary>
    public static List<string> ListValue(JsonNode? node) => node switch
    {
        null => [],
        JsonArray array => array.Select(item => item is null ? "None" : Str(item)).ToList(),
        _ => [Str(node)],
    };

    /// <summary>model_selection.value_enabled.</summary>
    public static bool ValueEnabled(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetValueKind() == JsonValueKind.True;
        }
        return NormalizeKey(node) is not ("" or "0" or "false" or "off" or "no");
    }

    /// <summary>
    /// Python's <c>round(value, digits)</c>: half-to-even on the exact binary
    /// value, returned as the double nearest the decimal result. Plain
    /// Math.Round scales first and can land on the other side of a midpoint.
    /// </summary>
    public static double Round(double value, int digits)
    {
        if (!double.IsFinite(value) || value == 0) return value;
        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;

        BigInteger numerator = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        BigInteger denominator = BigInteger.One;
        if (exponent > 0) numerator <<= exponent; else denominator <<= -exponent;

        BigInteger quotient = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        BigInteger twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven)) quotient += 1;

        double result = (double)quotient / Math.Pow(10, digits);
        return negative ? -result : result;
    }

    /// <summary>Python's <c>f"{value:g}"</c> for the magnitudes reason strings use.</summary>
    public static string FormatG(double value) =>
        value.ToString("G6", CultureInfo.InvariantCulture).Replace("E-0", "e-0").Replace("E+0", "e+");

    public static JsonNode? Get(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var node) ? node : null;
}
