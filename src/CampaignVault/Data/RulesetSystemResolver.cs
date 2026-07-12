using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Maps <see cref="SystemExtension"/> runtime types to <see cref="RulesetSystem"/>.
/// </summary>
public static class RulesetSystemResolver
{
    public static bool TryFromStats(SystemExtension stats, out RulesetSystem system)
    {
        system = stats switch
        {
            Dnd5eExtension => RulesetSystem.Dnd5e,
            Pf2eExtension => RulesetSystem.Pathfinder2e,
            _ => default
        };

        return stats is Dnd5eExtension or Pf2eExtension;
    }

    public static RulesetSystem FromStats(SystemExtension stats)
    {
        if (!TryFromStats(stats, out var system))
        {
            throw new NotSupportedException(
                $"Cannot resolve ruleset system from {stats.GetType().Name}.");
        }

        return system;
    }
}