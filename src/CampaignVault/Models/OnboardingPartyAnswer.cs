using System.Text.Json;

namespace CampaignVault.Models;

/// <summary>
/// The answer to the onboarding "party" question, carried as JSON text like every other answer:
/// <c>{ "mode": "build-now", "level": 1, "characterIds": ["..."], "companionIds": ["..."] }</c>.
/// </summary>
public sealed class OnboardingPartyAnswer
{
    public const string ModeBuildNow = "build-now";
    public const string ModeDmDrafts = "dm-drafts";
    public const string ModeBuildAtTable = "build-at-table";

    public static readonly IReadOnlyList<string> Modes = [ModeBuildNow, ModeDmDrafts, ModeBuildAtTable];

    public string Mode { get; init; } = ModeBuildAtTable;
    public int Level { get; init; } = 1;
    public IReadOnlyList<string> CharacterIds { get; init; } = [];
    public IReadOnlyList<string> CompanionIds { get; init; } = [];

    /// <summary>True when the player characters already exist as built characters.</summary>
    public bool HasBuiltCharacters => CharacterIds.Count > 0;

    /// <summary>Parses and checks the shape of an answer. Whether the ids exist is the caller's check.</summary>
    public static bool TryParse(string? text, out OnboardingPartyAnswer answer, out string error)
    {
        answer = new OnboardingPartyAnswer();
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Answer cannot be empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The party answer must be a JSON object: { mode, level, characterIds, companionIds }.";
                return false;
            }

            var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (!Modes.Contains(mode))
            {
                error = $"Invalid mode. Choose from: {string.Join(", ", Modes)}";
                return false;
            }

            var level = 1;
            if (root.TryGetProperty("level", out var l) && l.ValueKind != JsonValueKind.Null)
            {
                if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out level) || level < 1 || level > 20)
                {
                    error = "The party level must be a whole number between 1 and 20.";
                    return false;
                }
            }

            if (!TryReadIds(root, "characterIds", out var characters, out error)
                || !TryReadIds(root, "companionIds", out var companions, out error))
            {
                return false;
            }

            if (mode == ModeBuildNow && characters.Count == 0)
            {
                error = "build-now needs at least one built character id. Use build-at-table or dm-drafts when none are built yet.";
                return false;
            }

            answer = new OnboardingPartyAnswer { Mode = mode, Level = level, CharacterIds = characters, CompanionIds = companions };
            return true;
        }
        catch (JsonException)
        {
            error = "The party answer must be valid JSON: { mode, level, characterIds, companionIds }.";
            return false;
        }
    }

    private static bool TryReadIds(JsonElement root, string name, out List<string> ids, out string error)
    {
        ids = [];
        error = "";
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            error = $"{name} must be an array of ids.";
            return false;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(id))
            {
                error = $"{name} must hold non-empty id strings.";
                return false;
            }

            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                ids.Add(id);
            }
        }

        return true;
    }
}
