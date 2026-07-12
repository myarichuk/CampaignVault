using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// D&amp;D 5e multiclass spellcasting: combined caster level for standard spell slot pools.
/// Warlock pact magic is tracked separately via warlock_invocations.
/// </summary>
public static class Dnd5eCasterLevelHelper
{
    // Lazy fallback for call sites that don't inject a provider (tests, legacy code).
    private static ClassDefinitionProvider? _defaultProvider;

    private static ClassDefinitionProvider DefaultProvider =>
        _defaultProvider ??= new ClassDefinitionProvider(
            Path.Combine(Path.GetTempPath(), "cv_classdef_embedded"),
            typeof(ClassDefinitionProvider).Assembly);

    public static int ComputeCasterLevel(
        IReadOnlyList<ClassLevelEntry> classLevels,
        ClassDefinitionProvider? provider = null)
    {
        var classDefs = (provider ?? DefaultProvider).GetClassesForSystem(RulesetSystem.Dnd5e);
        var total = 0;

        foreach (var entry in classLevels)
        {
            var casterType = ResolveCasterType(entry.Class, classDefs);
            total += casterType switch
            {
                CasterType.Warlock => 0, // Pact Magic does not contribute to standard slots
                CasterType.Full => entry.Level,
                CasterType.Half => entry.Level / 2,
                CasterType.HalfRoundUp => (entry.Level + 1) / 2,
                CasterType.Third => entry.Level / 3,
                _ => 0
            };
        }

        return total;
    }

    private static CasterType ResolveCasterType(
        string className,
        IReadOnlyDictionary<string, ClassDefinition> classDefs)
    {
        ClassDefinition? bestMatch = null;
        var bestMatchLen = 0;

        foreach (var def in classDefs.Values)
        {
            foreach (var alias in def.Aliases)
            {
                if (alias.Length > bestMatchLen &&
                    className.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    bestMatch = def;
                    bestMatchLen = alias.Length;
                }
            }
        }

        return bestMatch?.CasterType ?? CasterType.None;
    }
}
