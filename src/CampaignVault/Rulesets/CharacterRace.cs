using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// A 5e character's race template and what it gives beyond the scores and traits stamped at creation: roll effects and
/// spells by level. Read when needed, so a data fix reaches every character.
/// </summary>
public static class CharacterRace
{
    public static RaceDefinition? Of(Character character, string system, RaceDefinitionProvider? races) =>
        races is not null && character.SystemStats is Dnd5eExtension { Race: { Length: > 0 } race }
        && races.TryGet(system, race, out var definition)
            ? definition
            : null;

    /// <summary>The race's roll effects, tagged with its name for the roll notes.</summary>
    public static IReadOnlyList<ActiveFeatEffect> Effects(Character character, string system, RaceDefinitionProvider? races) =>
        Of(character, system, races) is { } race ? [.. race.Effects.Select(e => new ActiveFeatEffect(race.Name, e))] : [];

    /// <summary>The spells the race gives up to <paramref name="characterLevel"/>.</summary>
    public static IReadOnlyList<string> Spells(Character character, string system, RaceDefinitionProvider? races, int characterLevel) =>
        Of(character, system, races) is { } race
            ? [.. race.Spells.Where(kv => kv.Key <= characterLevel).OrderBy(kv => kv.Key).SelectMany(kv => kv.Value)]
            : [];
}
