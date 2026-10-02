using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// A class feature as get_entity and the builder preview show it: its level, name, rule text, the option it came from
/// ("Hunter"), and the spells it gives so far (a domain's, up to the class level).
/// </summary>
public sealed record ClassFeatureView(int Level, string Name, string? Description, string? From, IReadOnlyList<string> Spells);

/// <summary>
/// A character's class features, read from its classes' progressions and its recorded level-up choices: the class's own
/// features up to its level, the features of the options it picked (a subclass's), and what those do (effects, hit points
/// per level, unarmored armor class). Read when needed, never stored, so a data fix reaches every character.
/// </summary>
public static class CharacterClassFeatures
{
    /// <summary>Each class the character has, with its progression and level. Classes with no progression are left out.</summary>
    public static IReadOnlyList<(ProgressionDefinition Progression, int Level)> Classes(
        Character character, string system, ProgressionDefinitionProvider? progressions)
    {
        if (progressions is null || character.SystemStats is null)
            return [];

        return
        [
            .. CharacterClassResolver.ResolveClassLevels(character)
                .Select(e => progressions.TryGetProgression(system, e.Class, out var p) ? (p, e.Level) : default)
                .Where(c => c.p is not null)
                .Select(c => (c.p!, c.Level)),
        ];
    }

    /// <summary>
    /// The picks the character recorded, as a progression walk wants them. A single class matches a pick by level and key;
    /// with several classes the recorded level is the character's, not the class's, so the key alone matches.
    /// </summary>
    public static Func<int, string, IEnumerable<string>> Picked(SystemExtension? stats, bool multiclass)
    {
        var records = stats?.LevelUpChoices ?? [];
        return (level, key) => records
            .Where(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && (multiclass || r.Level == level))
            .Select(r => r.Value);
    }

    public static IReadOnlyList<GainedFeature> Features(Character character, string system, ProgressionDefinitionProvider? progressions)
    {
        var classes = Classes(character, system, progressions);
        var picked = Picked(character.SystemStats, classes.Count > 1);
        return [.. classes.SelectMany(c => c.Progression.FeaturesUpTo(c.Level, picked))];
    }

    /// <summary>The features for display, in level order.</summary>
    public static IReadOnlyList<ClassFeatureView> Views(Character character, string system, ProgressionDefinitionProvider? progressions)
    {
        var classes = Classes(character, system, progressions);
        var picked = Picked(character.SystemStats, classes.Count > 1);
        return [.. classes.SelectMany(c => c.Progression.FeaturesUpTo(c.Level, picked).Select(f => View(f, c.Level))).OrderBy(v => v.Level)];
    }

    /// <summary>One feature for display; <paramref name="classLevel"/> limits the spells to those gained so far.</summary>
    public static ClassFeatureView View(GainedFeature f, int classLevel) =>
        new(f.Level, f.Feature.Name, f.Feature.Description,
            f.From is null ? null : f.From.Label,
            [.. f.Feature.Spells.Where(kv => kv.Key <= classLevel).OrderBy(kv => kv.Key).SelectMany(kv => kv.Value)]);

    /// <summary>The spells the character's features give outright (always prepared), up to each class's level.</summary>
    public static IReadOnlyList<string> GrantedSpells(Character character, string system, ProgressionDefinitionProvider? progressions)
    {
        var classes = Classes(character, system, progressions);
        var picked = Picked(character.SystemStats, classes.Count > 1);
        return
        [
            .. classes
                .SelectMany(c => c.Progression.FeaturesUpTo(c.Level, picked)
                    .SelectMany(f => f.Feature.Spells.Where(kv => kv.Key <= c.Level).SelectMany(kv => kv.Value)))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The spells <paramref name="options"/> (picked class options) add to the class's list at <paramref name="classLevel"/>.</summary>
    public static IReadOnlyList<string> ExpandedSpells(IEnumerable<ChoiceOption> options, int classLevel) =>
    [
        .. options
            .SelectMany(o => o.Features.Where(kv => kv.Key <= classLevel).SelectMany(kv => kv.Value))
            .SelectMany(f => f.SpellOptions.Where(kv => kv.Key <= classLevel).SelectMany(kv => kv.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// The roll effects the character's class features and picked options have (Archery's +2 on ranged attacks), tagged with
    /// the feature or option name for the roll notes.
    /// </summary>
    public static IReadOnlyList<ActiveFeatEffect> Effects(Character character, string system, ProgressionDefinitionProvider? progressions)
    {
        var classes = Classes(character, system, progressions);
        var picked = Picked(character.SystemStats, classes.Count > 1);
        var effects = new List<ActiveFeatEffect>();
        foreach (var (progression, level) in classes)
        {
            effects.AddRange(progression.FeaturesUpTo(level, picked)
                .SelectMany(f => f.Feature.Effects.Select(e => new ActiveFeatEffect(f.Feature.Name, e))));
            effects.AddRange(progression.PickedOptions(level, picked)
                .SelectMany(o => o.Effects.Select(e => new ActiveFeatEffect(o.Label, e))));
        }

        return effects;
    }

    /// <summary>Extra hit points the features give at <paramref name="characterLevel"/> (Draconic Resilience: 1 a level).</summary>
    public static int HpBonus(Character character, string system, ProgressionDefinitionProvider? progressions, int characterLevel) =>
        Features(character, system, progressions).Sum(f => f.Feature.HpPerLevel) * characterLevel;

    /// <summary>The best unarmored armor class a feature gives, or null when none does.</summary>
    public static int? UnarmoredArmorClass(
        Character character, string system, ProgressionDefinitionProvider? progressions, Func<string, int> abilityModifier)
    {
        var formulas = Features(character, system, progressions).Select(f => f.Feature.UnarmoredArmorClass).OfType<UnarmoredArmorClass>().ToList();
        return formulas.Count == 0 ? null : formulas.Max(f => f.Base + f.Abilities.Sum(abilityModifier));
    }

    /// <summary>The armor, weapon and tool proficiencies the character's class features and picked options' features give.</summary>
    public static IReadOnlyList<ProficiencyGrants> Proficiencies(Character character, string system, ProgressionDefinitionProvider? progressions) =>
        [.. Features(character, system, progressions).Select(f => f.Feature.Proficiencies).OfType<ProficiencyGrants>()];
}
