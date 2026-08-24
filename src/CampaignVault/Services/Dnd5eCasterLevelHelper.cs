using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// D&amp;D 5e multiclass spellcasting: combined caster level for standard spell slot pools.
/// Warlock pact magic is tracked separately via warlock_invocations.
/// </summary>
public static class Dnd5eCasterLevelHelper
{
    public static int ComputeCasterLevel(
        IReadOnlyList<ClassLevelEntry> classLevels,
        ClassDefinitionProvider? provider = null)
    {
        var classDefs = (provider ?? ClassAliasMatcher.DefaultProvider).GetClassesForSystem(RulesetSystem.Dnd5e);
        var total = 0;

        foreach (var entry in classLevels)
        {
            total += ClassAliasMatcher.ResolveCasterType(entry.Class, classDefs) switch
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
}
