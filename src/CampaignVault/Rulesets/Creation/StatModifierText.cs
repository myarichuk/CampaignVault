using System.Globalization;
using System.Text.Json;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// The text form of a <c>modifiers</c> stat block value, as the companion templates ship it ("Nature +4, Perception +5"),
/// so a template copied as-is checks and saves like the object form. Read only when every part is "name number".
/// </summary>
public static class StatModifierText
{
    public static bool TryParse(string? text, out JsonElement value)
    {
        value = default;
        var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cut = part.LastIndexOf(' ');
            if (cut <= 0 || !int.TryParse(part[(cut + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                return false;
            entries[part[..cut].Trim()] = n;
        }

        if (entries.Count == 0)
            return false;

        value = JsonSerializer.SerializeToElement(entries);
        return true;
    }

    /// <summary>The object form of a modifiers value: an object as it is, text read when it parses; anything else unchanged.</summary>
    public static JsonElement Normalize(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && TryParse(value.GetString(), out var parsed) ? parsed : value;
}
