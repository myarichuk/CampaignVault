using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>A feat a character has, and the level it was taken at (0 when it came another way: a race, the DM).</summary>
public sealed record TakenFeat(FeatDefinition Feat, int Level);

/// <summary>
/// What a 5e character's feats give beyond roll effects, read from its feat list and the picks recorded with them (key
/// <c>&lt;feat&gt;.ability</c>, <c>&lt;feat&gt;.spells</c>): proficiencies, saving throws, hit points and spells. Read when
/// needed, never stored, so a data fix reaches every character.
/// </summary>
public static class CharacterFeats
{
    public static IReadOnlyList<TakenFeat> Taken(Character character, string system, FeatDefinitionProvider? feats)
    {
        if (feats is null || character.SystemStats is not Dnd5eExtension stats)
            return [];

        return
        [
            .. stats.Feats.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => feats.TryGet(system, id, out var feat) ? new TakenFeat(feat, TakenAt(stats, feat.Name)) : null)
                .OfType<TakenFeat>(),
        ];
    }

    private static int TakenAt(SystemExtension stats, string feat) =>
        stats.LevelUpChoices.Where(r => r.Value.Equals(feat, StringComparison.OrdinalIgnoreCase)).Select(r => r.Level).DefaultIfEmpty(0).Min();

    /// <summary>The picks recorded for a feat's choice (<c>ability</c>, <c>spells</c>).</summary>
    public static IEnumerable<string> Picks(SystemExtension stats, FeatDefinition feat, string choice) =>
        stats.LevelUpChoices
            .Where(r => r.Key.Equals($"{feat.Name}.{choice}", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value);

    /// <summary>The ability a half-feat raises with no pick to make, and by how much; empty otherwise.</summary>
    public static IReadOnlyList<(string Ability, int Amount)> FixedIncrease(FeatDefinition? feat) =>
        feat?.AbilityIncrease is { Choose: [var only] } increase
        && Rulesets.Creation.CreationSources.AbilityNames.FirstOrDefault(a => a.Equals(only, StringComparison.OrdinalIgnoreCase)) is { } ability
            ? [(ability, increase.Amount)]
            : [];

    /// <summary>Extra hit points at <paramref name="characterLevel"/>: each feat's per-level bonus, once it is taken.</summary>
    public static int HpBonus(Character character, string system, FeatDefinitionProvider? feats, int characterLevel) =>
        Taken(character, system, feats).Where(t => t.Level <= characterLevel).Sum(t => t.Feat.HpPerLevel) * characterLevel;

    public static IReadOnlyList<ProficiencyGrants> Proficiencies(Character character, string system, FeatDefinitionProvider? feats) =>
        [.. Taken(character, system, feats).Select(t => t.Feat.Proficiencies).OfType<ProficiencyGrants>()];

    /// <summary>The saving throws the feats give: their own, and the one of the ability a feat raised when it says so.</summary>
    public static IReadOnlyList<string> SavingThrows(Character character, string system, FeatDefinitionProvider? feats)
    {
        var saves = new List<string>();
        foreach (var (feat, _) in Taken(character, system, feats))
        {
            saves.AddRange(feat.SavingThrows);
            if (feat.SavingThrowOfIncrease)
            {
                saves.AddRange(FixedIncrease(feat).Select(i => i.Ability));
                saves.AddRange(Picks(character.SystemStats, feat, "ability"));
            }
        }

        return [.. saves.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The spells the feats give: their own and the ones picked for them.</summary>
    public static IReadOnlyList<string> Spells(Character character, string system, FeatDefinitionProvider? feats) =>
    [
        .. Taken(character, system, feats)
            .SelectMany(t => t.Feat.Spells.Concat(Picks(character.SystemStats, t.Feat, "spells")))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];
}
