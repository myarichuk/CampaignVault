using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>PF2e classes that use standard spell slot pools.</summary>
public static class Pf2eCasterClasses
{
    private static ClassDefinitionProvider? _defaultProvider;

    private static ClassDefinitionProvider DefaultProvider =>
        _defaultProvider ??= new ClassDefinitionProvider(
            Path.Combine(Path.GetTempPath(), "cv_classdef_embedded"),
            typeof(ClassDefinitionProvider).Assembly);

    public static bool HasCaster(
        IReadOnlyList<ClassLevelEntry> classLevels,
        ClassDefinitionProvider? provider = null)
    {
        var classDefs = (provider ?? DefaultProvider).GetClassesForSystem(RulesetSystem.Pathfinder2e);

        foreach (var entry in classLevels)
        {
            var bestMatchLen = 0;
            CasterType? resolved = null;

            foreach (var def in classDefs.Values)
            {
                foreach (var alias in def.Aliases)
                {
                    if (alias.Length > bestMatchLen &&
                        entry.Class.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = def.CasterType;
                        bestMatchLen = alias.Length;
                    }
                }
            }

            if (resolved is not null and not CasterType.None)
                return true;
        }

        return false;
    }
}
