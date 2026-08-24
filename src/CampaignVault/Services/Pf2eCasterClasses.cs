using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>PF2e classes that use standard spell slot pools.</summary>
public static class Pf2eCasterClasses
{
    public static bool HasCaster(
        IReadOnlyList<ClassLevelEntry> classLevels,
        ClassDefinitionProvider? provider = null)
    {
        var classDefs = (provider ?? ClassAliasMatcher.DefaultProvider)
            .GetClassesForSystem(RulesetSystem.Pathfinder2e);

        return classLevels.Any(entry =>
            ClassAliasMatcher.ResolveCasterType(entry.Class, classDefs) != CasterType.None);
    }
}
