using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

internal sealed class SceneFactionSummaryFactory
{
    public List<FactionPresenceSummary> Create(
        IEnumerable<Faction> relevantFactions,
        IReadOnlyList<Character> presentNpcs)
    {
        return relevantFactions.Select(faction =>
        {
            int? reputation = null;
            var playerRepChar = presentNpcs.FirstOrDefault(npc => npc.Social.FactionReputations.ContainsKey(faction.Id));
            if (playerRepChar != null)
            {
                reputation = playerRepChar.Social.FactionReputations[faction.Id];
            }

            return new FactionPresenceSummary(
                faction.Id,
                faction.Name,
                faction.InfluenceLevel,
                DetermineLocalStance(faction, relevantFactions),
                reputation,
                faction.TerritoryLocationIds.Count,
                faction.EconomicDemand);
        }).ToList();
    }

    private static FactionStance DetermineLocalStance(Faction faction, IEnumerable<Faction> relevantFactions)
    {
        var localStance = FactionStance.Neutral;
        if (faction.StanceToward == null)
        {
            return localStance;
        }

        foreach (var other in relevantFactions)
        {
            if (other.Id == faction.Id)
            {
                continue;
            }

            if (!faction.StanceToward.TryGetValue(other.Id, out var stance))
            {
                continue;
            }

            if (stance == FactionStance.AtWar)
            {
                return FactionStance.AtWar;
            }

            if (stance == FactionStance.Hostile && localStance != FactionStance.AtWar)
            {
                localStance = FactionStance.Hostile;
            }

            if (stance == FactionStance.Allied && localStance == FactionStance.Neutral)
            {
                localStance = FactionStance.Allied;
            }
        }

        if (faction.StanceToward.TryGetValue("party", out var partyStance) && partyStance == FactionStance.Opportunistic)
        {
            localStance = FactionStance.Opportunistic;
        }

        return localStance;
    }
}
