using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// D&amp;D 5e caster level for standard spell slot pools: a single casting class's own table, or the multiclass table.
/// Warlock pact magic is tracked separately via warlock_invocations.
/// </summary>
public static class Dnd5eCasterLevelHelper
{
    public static int ComputeCasterLevel(
        IReadOnlyList<ClassLevelEntry> classLevels,
        ClassDefinitionProvider? provider = null)
    {
        var classDefs = (provider ?? ClassAliasMatcher.DefaultProvider).GetClassesForSystem(RulesetSystem.Dnd5e);
        var casters = classLevels
            .Select(entry => (entry.Level, Type: ClassAliasMatcher.ResolveCasterType(entry.Class, classDefs)))
            .Where(c => c.Type is not (CasterType.None or CasterType.Warlock)) // Pact Magic does not contribute to standard slots
            .ToList();

        // A single spellcasting class uses its own slot table: a half caster rounds up from level 2 (paladin 5 = the
        // full caster's level 3), a third caster from level 3. Only two or more casting classes use the multiclass
        // table, which rounds each class down.
        if (casters.Count == 1)
        {
            var (level, type) = casters[0];
            return type switch
            {
                CasterType.Full => level,
                CasterType.Half => level < 2 ? 0 : (level + 1) / 2,
                CasterType.HalfRoundUp => (level + 1) / 2,
                CasterType.Third => level < 3 ? 0 : (level + 2) / 3,
                _ => 0
            };
        }

        return casters.Sum(c => c.Type switch
        {
            CasterType.Full => c.Level,
            CasterType.Half => c.Level / 2,
            CasterType.HalfRoundUp => (c.Level + 1) / 2,
            CasterType.Third => c.Level / 3,
            _ => 0
        });
    }
}
