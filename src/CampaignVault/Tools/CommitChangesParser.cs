using System.Text.Json;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Parses commit <c>changes</c> from MCP JSON payloads. The MCP binder cannot reliably
/// materialize polymorphic <see cref="WorldChange"/> arrays directly.
/// </summary>
internal static class CommitChangesParser
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
    };

    public static bool TryParse(JsonElement? changes, out WorldChange[]? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (changes is null)
        {
            return false;
        }

        var el = changes.Value;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        try
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var text = el.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                parsed = JsonSerializer.Deserialize<WorldChange[]>(text, Options);
                return parsed is { Length: > 0 };
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                parsed = JsonSerializer.Deserialize<WorldChange[]>(el.GetRawText(), Options);
                return parsed is { Length: > 0 };
            }

            error = $"Expected 'changes' to be a JSON array, but received {el.ValueKind}.";
            return false;
        }
        catch (JsonException ex)
        {
            var source = el.ValueKind == JsonValueKind.Array ? el : default(JsonElement?);
            error = CommitJsonErrorHints.Enrich(ex, source);
            return false;
        }
    }
}