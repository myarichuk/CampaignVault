namespace CampaignVault.Data;

/// <summary>
/// Narrative-only nudge for ItemDetails that haven't been touched in a while — surfaces a hint for
/// the DM-LLM to reconsider relevance (fade a scratch, discover a compartment, etc.) without ever
/// auto-mutating or evicting the record itself. Mirrors AmbientItemDecayRule's engine-detects/
/// LLM-decides split.
///
/// The check interval is per-detail (ItemDetail.ReviewIntervalDays, set by whoever authored it —
/// a punctured waterskin might warrant a 1-day check, a scorch mark 90 days) rather than one global
/// constant, since how fast a detail plausibly changes varies wildly by what it actually is.
/// </summary>
public class ItemDetailStalenessRule : ISimulationRule
{
    public string Name => "Item Detail Staleness (narrative nudge)";

    // Runs alongside AmbientItemDecay(90), just before TransientEviction(100).
    public int Order => 91;

    /// <summary>Fallback when a detail doesn't specify its own ReviewIntervalDays.</summary>
    public const int DefaultStaleDays = 60;

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();

        var items = await SimulationQueryHelper.QueryCampaignItemsAsync(context.Session, context.CampaignName, 200, ct);
        var currentDay = context.Time.TotalDaysElapsed;

        foreach (var item in items)
        {
            foreach (var detail in item.ItemDetails.Where(d => !d.IsRetired))
            {
                var interval = detail.ReviewIntervalDays ?? DefaultStaleDays;
                var daysSince = currentDay - detail.UpdatedOnDay;
                if (daysSince >= interval)
                {
                    narratives.Add(new RuleNarrative(
                        $"'{detail.Name}' on '{item.Name}' hasn't been revisited in {daysSince} days (review interval: {interval}) — consider whether it's still relevant or has changed.",
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
