namespace CampaignVault.Data;

/// <summary>
/// Narrative-only nudge for ItemDetails that haven't been touched in a while — surfaces a hint for
/// the DM-LLM to reconsider relevance (fade a scratch, discover a compartment, etc.) without ever
/// auto-mutating or evicting the record itself. Mirrors AmbientItemDecayRule's engine-detects/
/// LLM-decides split.
/// </summary>
public class ItemDetailStalenessRule : ISimulationRule
{
    public string Name => "Item Detail Staleness (narrative nudge)";

    // Runs alongside AmbientItemDecay(90), just before TransientEviction(100).
    public int Order => 91;

    private const int StaleDays = 60;

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();

        var items = await SimulationQueryHelper.QueryCampaignItemsAsync(context.Session, context.CampaignName, 200, ct);
        var currentDay = context.Time.TotalDaysElapsed;

        foreach (var item in items)
        {
            foreach (var detail in item.ItemDetails.Where(d => !d.IsRetired))
            {
                if (currentDay - detail.UpdatedOnDay >= StaleDays)
                {
                    narratives.Add(new RuleNarrative(
                        $"'{detail.Name}' on '{item.Name}' hasn't been revisited in a while — consider whether it's still relevant.",
                        Persist: false));
                }
            }
        }

        // TODO(item-detail-pruning): unbounded ItemDetails growth is NOT addressed here — no auto-mutation,
        // no auto-eviction/archival of ItemDetail records in v1. Judged low-risk (est. low-single-digit MB
        // over a long campaign given typical detail counts); revisit if usage data says otherwise. This
        // narrative-emission point is the natural place to add an eviction WorldChange + handler later.

        return new RuleResult(narratives, []);
    }
}
