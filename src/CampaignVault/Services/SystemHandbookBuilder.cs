using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Formats ruleset YAML registries into the <see cref="SystemHandbookResponse"/> discovery payload.
/// </summary>
public static class SystemHandbookBuilder
{
    public const string SpellDiscoveryNote =
        "For spell lists by class and level, call get_spells (use level filter and offset/limit pagination — full lists are large).";

    private static readonly IReadOnlyDictionary<string, string[]> CoverageNotes = new Dictionary<string, string[]>
    {
        [RulesetSystem.Pathfinder2e] =
        [
            "PF2e classes and spells cover ORC Player Core content shipped in this build.",
            "Remaster classes not in the free ORC set (e.g. oracle, psychic, magus) are omitted — add homebrew class YAML on disk.",
            "Spell class lists are derived from tradition mapping; verify edge cases with official sources when in doubt."
        ],
        [RulesetSystem.Dnd5e] =
        [
            "D&D 5e spells are the SRD 5.1 set (~319). Non-SRD spells (e.g. hex) are not included."
        ]
    };

    public static SystemHandbookResponse Build(
        string system,
        ClassDefinitionProvider classProvider,
        RaceDefinitionProvider raceProvider,
        BackgroundDefinitionProvider backgroundProvider,
        FeatDefinitionProvider featProvider,
        ConditionDefinitionProvider conditionProvider,
        CreatureDefinitionProvider? creatureProvider = null,
        IReadOnlyList<CustomFeat>? homebrewFeats = null)
    {
        var classes = classProvider.GetClassesForSystem(system)
            .Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToClassEntry)
            .ToList();

        var races = raceProvider.GetRacesForSystem(system)
            .Keys
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backgrounds = backgroundProvider.GetBackgroundsForSystem(system)
            .Keys
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var feats = featProvider.GetFeatsForSystem(system)
            .Keys
            .Union((homebrewFeats ?? []).Where(f => !f.IsArchived).Select(f => f.Name), StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var conditions = conditionProvider.GetConditionsForSystem(system)
            .Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToConditionEntry)
            .ToList();

        var creatureList = (creatureProvider?.GetCreaturesForSystem(system) ?? new Dictionary<string, CreatureDefinition>())
            .Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var creatures = new CreatureHandbookSummary
        {
            TotalCount = creatureList.Count,
            ExampleNames = creatureList.Take(5).Select(c => c.Name).ToList(),
            Hint = "Use query_creatures for the full paginated list (SRD + campaign homebrew merged).",
        };

        var notes = SpellDiscoveryNote;
        if (CoverageNotes.TryGetValue(system, out var coverage))
        {
            notes += " " + string.Join(" ", coverage);
        }

        return new SystemHandbookResponse
        {
            System = system.ToSlug(),
            Classes = classes,
            Races = races,
            Backgrounds = backgrounds,
            Feats = feats,
            Conditions = conditions,
            Creatures = creatures,
            Notes = notes,
        };
    }

    private static ClassHandbookEntry ToClassEntry(ClassDefinition def) =>
        new()
        {
            Name = def.Name,
            CasterType = (def.CasterType ?? CasterType.None).ToString(),
            HitDie = def.HitDie,
            Pools = def.Pools,
        };

    private static ConditionHandbookEntry ToConditionEntry(ConditionDefinition def) =>
        new()
        {
            Name = def.Name,
            DurationType = (def.DurationType ?? ConditionDurationType.Manual).ToString(),
            MechanicalSummary = def.MechanicalSummary,
            MoodHint = def.MoodHint,
        };

}