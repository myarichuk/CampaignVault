using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// The porridge-plate case: detects ambient items whose LLM-authored Persistence.ExpiresAtDay has
/// passed and flips PressureSurfaced once (idempotent — never fires twice for the same expiry).
/// Never moves, archives, or deletes the item itself — AmbientItemExpiryPressureContributor surfaces
/// the nag and the DM-LLM decides the item's fate via a follow-up commit, matching StatusEffect's
/// engine-detects/LLM-decides authorship split.
/// </summary>
public class AmbientItemDecayRule : ISimulationRule
{
    public string Name => "Ambient Item Decay (tidy-away nag)";

    // Runs after RumorDecay(20)/QuestStaleness(45), just before TransientEviction(100).
    public int Order => 90;

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
        var deltas = new List<WorldChange>();

        var items = await SimulationQueryHelper.QueryCampaignItemsAsync(context.Session, context.CampaignName, 200, ct);
        var currentDay = context.Time.TotalDaysElapsed;

        foreach (var item in items)
        {
            var persistence = item.Persistence;
            if (persistence == null || persistence.PressureSurfaced || !persistence.ExpiresAtDay.HasValue)
            {
                continue;
            }

            if (currentDay < persistence.ExpiresAtDay.Value)
            {
                continue;
            }

            deltas.Add(new ItemPersistenceSurfaced { ItemId = item.Id });

            var suffix = string.IsNullOrWhiteSpace(persistence.Note) ? "." : $" ({persistence.Note}).";
            narratives.Add(new RuleNarrative($"'{item.Name}' has lingered past its expected time{suffix}", Persist: false));
        }

        return new RuleResult(narratives, deltas);
    }
}
