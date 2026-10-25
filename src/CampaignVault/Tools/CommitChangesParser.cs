using System.Text.Json;
using CampaignVault.Models;
using CampaignVault.Models.Converters;
using CampaignVault.Plugins;

namespace CampaignVault.Tools;

/// <summary>
/// Parses commit <c>changes</c> from MCP JSON payloads. The MCP binder cannot reliably
/// materialize polymorphic <see cref="WorldChange"/> arrays directly.
/// </summary>
internal static class CommitChangesParser
{
    private static readonly object OptionsGate = new();
    private static JsonSerializerOptions? _options;
    private static int _optionsRegistryVersion = -1;

    internal static JsonSerializerOptions Options
    {
        get
        {
            lock (OptionsGate)
            {
                var version = WorldChangeTypeRegistry.Instance.Version;
                if (_options is null || _optionsRegistryVersion != version)
                {
                    _options = CreateOptions();
                    _optionsRegistryVersion = version;
                }

                return _options;
            }
        }
    }

    /// <summary>
    /// Strict on purpose: an unknown field name fails the batch with a message naming the field and the
    /// type's real fields, instead of being dropped without a word (the tools/list stub advertises no
    /// per-type fields, so a silent drop would be undiagnosable).
    /// </summary>
    internal static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = WorldChangeTypeRegistry.Instance.CreateTypeInfoResolver(),
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

                text = WorldChangeNormalizer.NormalizeChangesArray(text);
                parsed = JsonSerializer.Deserialize<WorldChange[]>(text, Options);
                return parsed is { Length: > 0 };
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                var text = WorldChangeNormalizer.NormalizeChangesArray(el.GetRawText());
                parsed = JsonSerializer.Deserialize<WorldChange[]>(text, Options);
                return parsed is { Length: > 0 };
            }

            error = $"Expected 'changes' to be a JSON array, but received {el.ValueKind}.";
            return false;
        }
        catch (JsonException ex)
        {
            var source = el.ValueKind == JsonValueKind.Array ? el : default(JsonElement?);
            error = CommitBindingError.Describe(ex, source);
            return false;
        }
    }
}
