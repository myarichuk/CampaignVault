using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>One status on a character, resolved to its condition definition and level, with the effects in force.</summary>
internal sealed record ActiveCondition(StatusEffect Status, ConditionDefinition Definition, int Level)
{
    public IEnumerable<ConditionEffect> Effects => Definition.EffectsAt(Level);
}

/// <summary>
/// Reads condition definitions for the mechanics that follow from them: which statuses are conditions, which are suppressed by
/// another, and what each does. The data is the rule; this is the interpretation (name matching, levels, suppression).
/// </summary>
internal static class ConditionMechanics
{
    /// <summary>
    /// The definition a status stands for: its <c>ConditionName</c>, else its name (matching ignores case, spaces and
    /// punctuation), else its name without a trailing level ("Exhaustion 3" is level 3 of exhaustion). Stored data is untouched.
    /// </summary>
    public static bool TryResolve(
        IReadOnlyDictionary<string, ConditionDefinition> definitions,
        StatusEffect status,
        out ConditionDefinition definition,
        out int level)
    {
        level = 1;
        definition = null!;
        foreach (var candidate in Candidates(status))
        {
            var key = StatusEffectModifierProvider.Normalize(candidate.Name);
            if (key.Length == 0)
                continue;
            var found = Find(definitions, key);
            if (found is null)
                continue;
            definition = found;
            level = candidate.Level ?? 1;
            return true;
        }

        return false;
    }

    private static IEnumerable<(string Name, int? Level)> Candidates(StatusEffect status)
    {
        if (!string.IsNullOrWhiteSpace(status.ConditionName))
        {
            yield return (status.ConditionName, ParseLevel(status.Name, out _));
        }

        if (string.IsNullOrWhiteSpace(status.Name))
            yield break;

        yield return (status.Name, null);
        var level = ParseLevel(status.Name, out var stem);
        if (level is not null)
            yield return (stem, level);
    }

    /// <summary>"Exhaustion 3" gives 3 and the stem "Exhaustion"; a name with no trailing number gives null.</summary>
    private static int? ParseLevel(string? name, out string stem)
    {
        stem = name ?? "";
        if (name is not null && ConditionExpiryEvaluator.TryParseStackLevel(name.Trim(), out var level) && level > 0)
        {
            stem = name.Trim()[..name.Trim().LastIndexOf(' ')];
            return level;
        }

        return null;
    }

    private static ConditionDefinition? Find(IReadOnlyDictionary<string, ConditionDefinition> definitions, string normalizedKey)
    {
        foreach (var (key, def) in definitions)
        {
            if (StatusEffectModifierProvider.Normalize(key) == normalizedKey)
                return def;
        }

        return null;
    }

    /// <summary>
    /// The conditions on a character that are in force: each status resolved, minus any that another active condition
    /// suppresses (unconscious suppresses incapacitated and prone).
    /// </summary>
    public static IReadOnlyList<ActiveCondition> Active(Character? character, ConditionDefinitionProvider? provider, string? system)
    {
        if (character?.SystemStats?.StatusEffects is not { Count: > 0 } statuses || provider is null || string.IsNullOrWhiteSpace(system))
            return [];

        var definitions = provider.GetConditionsForSystem(system);
        var resolved = new List<ActiveCondition>();
        foreach (var status in statuses)
        {
            if (TryResolve(definitions, status, out var def, out var level))
                resolved.Add(new ActiveCondition(status, def, level));
        }

        // The same condition twice ("Exhaustion 3" and "Exhaustion 4", a DM adding a level without removing the old one) is one
        // condition at its highest level, so multiplicative effects (halved speed) never apply twice.
        if (resolved.Count > 1)
        {
            resolved = resolved
                .GroupBy(c => StatusEffectModifierProvider.Normalize(c.Definition.Name))
                .Select(g => g.OrderByDescending(c => c.Level).First())
                .ToList();
        }

        if (resolved.Count < 2 || resolved.All(c => c.Definition.Suppresses.Count == 0))
            return resolved;

        var suppressed = resolved
            .SelectMany(c => c.Definition.Suppresses)
            .Select(StatusEffectModifierProvider.Normalize)
            .ToHashSet();
        return resolved
            .Where(c => !suppressed.Contains(StatusEffectModifierProvider.Normalize(c.Definition.Name)))
            .ToList();
    }

    /// <summary>
    /// The hit point maximum in force: the stored maximum scaled by any <c>maxHpMultiplier</c> condition (exhaustion 4 halves it).
    /// An effective cap, not a stored change, so removing the condition restores the maximum.
    /// </summary>
    public static int EffectiveMaxHp(Character character, ConditionDefinitionProvider? provider)
    {
        if (character.MaxHp <= 0 || character.SystemStats is null
            || !RulesetSystemResolver.TryFromStats(character.SystemStats, out var system))
            return character.MaxHp;

        var factor = Active(character, provider, system)
            .SelectMany(c => c.Effects)
            .Where(e => e.Kind == ConditionEffectKinds.MaxHpMultiplier && e.Multiplier is not null)
            .Aggregate(1.0, (acc, e) => acc * e.Multiplier!.Value);
        return factor >= 1.0 ? character.MaxHp : Math.Max(1, (int)Math.Floor(character.MaxHp * factor));
    }

    /// <summary>
    /// Whether the character has the named condition, directly or because another active condition implies it (PF2e: prone,
    /// grabbed, restrained and paralyzed all make a creature off-guard).
    /// </summary>
    public static bool Has(Character character, ConditionDefinitionProvider? provider, string? system, string condition)
    {
        var wanted = StatusEffectModifierProvider.Normalize(condition);
        return Active(character, provider, system).Any(c =>
            StatusEffectModifierProvider.Normalize(c.Definition.Name) == wanted
            || c.Effects.Any(e => e.Kind == ConditionEffectKinds.Implies && StatusEffectModifierProvider.Normalize(e.Subject) == wanted));
    }

    /// <summary>The active condition whose effects at its level include <c>death</c> (exhaustion 6), or null.</summary>
    public static ActiveCondition? Fatal(Character character, ConditionDefinitionProvider? provider)
    {
        if (character.SystemStats is null || !RulesetSystemResolver.TryFromStats(character.SystemStats, out var system))
            return null;
        return Active(character, provider, system).FirstOrDefault(c => c.Effects.Any(e => e.Kind == ConditionEffectKinds.Death));
    }

    private static readonly Dictionary<string, string> AbilityStems = new()
    {
        ["strength"] = "str", ["str"] = "str", ["dexterity"] = "dex", ["dex"] = "dex", ["constitution"] = "con", ["con"] = "con",
        ["intelligence"] = "int", ["int"] = "int", ["wisdom"] = "wis", ["wis"] = "wis", ["charisma"] = "cha", ["cha"] = "cha",
    };

    /// <summary>True when two subject names are the same skill or the same ability ("Dexterity" and "dex" match; "Intimidation" and "Intelligence" don't).</summary>
    public static bool SameSubject(string? a, string? b)
    {
        var na = StatusEffectModifierProvider.Normalize(a);
        var nb = StatusEffectModifierProvider.Normalize(b);
        if (na == nb)
            return true;
        return AbilityStems.TryGetValue(na, out var sa) && AbilityStems.TryGetValue(nb, out var sb) && sa == sb;
    }
}
