using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Resolves the name of a saving throw as a model wrote it ("dex", "Dex save", "fortitude", "CON") to the one the
/// ruleset rolls, and refuses anything else. The resolvers used to default an omitted save (to Dexterity, or to
/// Constitution, or to Reflex, depending on which code path ran) and to roll an unknown name at +0, so a typo or a
/// 5e name on a PF2e character produced a confident, wrong number. Now every name is either canonical or an error.
/// </summary>
internal static class SavingThrowNames
{
    private static readonly string[] Dnd5eSaves = ["Strength", "Dexterity", "Constitution", "Intelligence", "Wisdom", "Charisma"];
    private static readonly string[] Pf2eSaves = ["Fortitude", "Reflex", "Will"];

    private static readonly Dictionary<string, string> Dnd5eAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["str"] = "Strength", ["strength"] = "Strength",
        ["dex"] = "Dexterity", ["dexterity"] = "Dexterity",
        ["con"] = "Constitution", ["constitution"] = "Constitution",
        ["int"] = "Intelligence", ["intelligence"] = "Intelligence",
        ["wis"] = "Wisdom", ["wisdom"] = "Wisdom",
        ["cha"] = "Charisma", ["charisma"] = "Charisma",
    };

    private static readonly Dictionary<string, string> Pf2eAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fort"] = "Fortitude", ["fortitude"] = "Fortitude",
        ["ref"] = "Reflex", ["reflex"] = "Reflex",
        ["will"] = "Will",
    };

    /// <summary>PF2e has three saves keyed to three abilities; a 5e-style ability name maps to its save, and the note says so.</summary>
    private static readonly Dictionary<string, string> Pf2eAbilityToSave = new(StringComparer.OrdinalIgnoreCase)
    {
        ["con"] = "Fortitude", ["constitution"] = "Fortitude",
        ["dex"] = "Reflex", ["dexterity"] = "Reflex",
        ["wis"] = "Will", ["wisdom"] = "Will",
    };

    /// <summary>The names a ruleset accepts, for error messages.</summary>
    public static string ValidList(string system) =>
        RulesetSystem.Canonicalize(system) switch
        {
            RulesetSystem.Pathfinder2e => string.Join(", ", Pf2eSaves),
            RulesetSystem.Dnd5e => string.Join(", ", Dnd5eSaves),
            _ => "the ruleset's saving throws",
        };

    /// <summary>
    /// True when <paramref name="requested"/> names a save the ruleset rolls. <paramref name="canonical"/> is the
    /// name to roll and show; <paramref name="note"/> is set when the input was rewritten in a way the reader should
    /// see (an ability name mapped to a PF2e save), never for plain casing or abbreviation.
    /// </summary>
    public static bool TryResolve(string system, string? requested, out string canonical, out string? note)
    {
        canonical = string.Empty;
        note = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        var key = Normalize(requested);
        switch (RulesetSystem.Canonicalize(system))
        {
            case RulesetSystem.Pathfinder2e:
                if (Pf2eAliases.TryGetValue(key, out var save))
                {
                    canonical = save;
                    return true;
                }

                if (Pf2eAbilityToSave.TryGetValue(key, out var mapped))
                {
                    canonical = mapped;
                    note = $"{requested.Trim()} read as the {mapped} save";
                    return true;
                }

                return false;

            case RulesetSystem.Dnd5e:
                if (Dnd5eAliases.TryGetValue(key, out var ability))
                {
                    canonical = ability;
                    return true;
                }

                return false;

            default:
                // A ruleset without a fixed save list (plugins, narrative) takes the name as given.
                canonical = requested.Trim();
                return true;
        }
    }

    /// <summary>Trims, drops a trailing "save"/"saving throw", and collapses punctuation: "Dex. Save" → "dex".</summary>
    private static string Normalize(string requested)
    {
        var s = requested.Trim().ToLowerInvariant();
        foreach (var suffix in new[] { "saving throw", "saving-throw", "save" })
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                s = s[..^suffix.Length];
                break;
            }
        }

        return new string(s.Where(char.IsLetter).ToArray());
    }
}
