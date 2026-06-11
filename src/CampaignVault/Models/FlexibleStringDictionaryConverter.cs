using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Deserializes dictionary values as strings even when LLM clients send numbers or booleans.
/// All values are normalized to strings so downstream resolvers can parse them consistently.
/// </summary>
public sealed class FlexibleStringDictionaryConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object for string dictionary.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a property name in string dictionary.");
            }

            var key = reader.GetString() ?? string.Empty;
            reader.Read();
            result[key] = CoerceToString(ref reader);
        }

        throw new JsonException("Unexpected end of JSON while reading string dictionary.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, entryValue) in value)
        {
            writer.WriteString(key, entryValue);
        }
        writer.WriteEndObject();
    }

    private static string CoerceToString(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.TryGetInt64(out var integer)
                ? integer.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException($"Unsupported token type '{reader.TokenType}' in string dictionary value.")
        };
}