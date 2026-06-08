using CampaignVault.Data.Initiative;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Re-arms suppressed persistent relationship initiatives when the bond is still in band
/// and enough days have passed since the initiative was last surfaced.
/// </summary>
public sealed class RelationalRearmRule(
    IInitiativeSuppressionStore suppressionStore,
    CampaignDocumentKeys keys) : ISimulationRule
{
    public string Name => "Relational Initiative Re-arm";
    public int Order => 50;

    public async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        if (string.IsNullOrWhiteSpace(context.CampaignName))
        {
            return new RuleResult(narratives, []);
        }

        var interval = context.Config?.RelationalRearmIntervalDays ?? 7;
        if (interval <= 0)
        {
            return new RuleResult(narratives, []);
        }

        var campaign = await context.Session.LoadAsync<Campaign>(keys.Meta(context.CampaignName), ct);
        if (campaign == null)
        {
            return new RuleResult(narratives, []);
        }

        var currentDay = context.Time.TotalDaysElapsed;

        foreach (var npc in context.ScheduledNpcs)
        {
            if (npc.Social?.Relationships is not { Count: > 0 } relationships)
            {
                continue;
            }

            foreach (var (targetId, value) in relationships)
            {
                var initiativeKey = RelationalInitiativeKeys.TryGetPersistentKey(npc.Id, targetId, value);
                if (initiativeKey == null)
                {
                    continue;
                }

                var suppressionKey = NpcInitiativeService.BuildSuppressionKey(npc.Id, initiativeKey);
                if (!campaign.InitiativeSurfaced.TryGetValue(suppressionKey, out var state) || !state.Consumed)
                {
                    continue;
                }

                if (currentDay - state.SurfacedDay < interval)
                {
                    continue;
                }

                suppressionStore.ReArm(campaign, suppressionKey);
                narratives.Add($"{npc.Name}'s persistent feelings toward a companion may resurface.");
            }
        }

        return new RuleResult(narratives, []);
    }
}