using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// PF2e skill increases (Player Core): an increase raises one skill a rank, untrained to trained or trained to expert at
/// any level, expert to master from level 7, master to legendary from 15. Where the increases come is data (the class
/// progression's SkillIncrease choices); these thresholds are the same for every class, so they are code.
/// </summary>
public static class Pf2eSkillRanks
{
    public const int MasterFrom = 7;
    public const int LegendaryFrom = 15;

    /// <summary>
    /// Raises <paramref name="skill"/> one rank in <paramref name="ranks"/> for an increase at <paramref name="level"/>, or
    /// leaves it and says why not.
    /// </summary>
    public static string? Raise(IDictionary<string, Pf2eProficiencyRank> ranks, string skill, int level)
    {
        var now = ranks.TryGetValue(skill, out var rank) ? rank : Pf2eProficiencyRank.Untrained;
        Pf2eProficiencyRank? next = now switch
        {
            Pf2eProficiencyRank.Untrained => Pf2eProficiencyRank.Trained,
            Pf2eProficiencyRank.Trained => Pf2eProficiencyRank.Expert,
            Pf2eProficiencyRank.Expert => Pf2eProficiencyRank.Master,
            Pf2eProficiencyRank.Master => Pf2eProficiencyRank.Legendary,
            _ => null,
        };
        if (next is null)
            return $"{skill} is already legendary.";
        if (next == Pf2eProficiencyRank.Master && level < MasterFrom)
            return $"{skill} is already expert; master needs level {MasterFrom}.";
        if (next == Pf2eProficiencyRank.Legendary && level < LegendaryFrom)
            return $"{skill} is already master; legendary needs level {LegendaryFrom}.";

        ranks[skill] = next.Value;
        return null;
    }
}
