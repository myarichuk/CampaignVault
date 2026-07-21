using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Archives (deletes) forgotten rumors that have aged beyond the configured threshold.
/// Runs after RumorDecayRule to clean up stale rumors that are no longer relevant.
/// </summary>
public class RumorArchivalRule : ISimulationRule
{
    public string Name => "Rumor Archival";
    public int Order => 21; // after RumorDecayRule (20)

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<RuleNarrative>();
        var archiveAfterDays = context.Config?.RumorArchiveAfterDays ?? 30;
        var currentDay = context.Time.TotalDaysElapsed;
        var archiveThresholdDay = currentDay - archiveAfterDays;

        var forgottenRumors = await context.Session.Query<Rumor>()
            .Where(r => r.State == RumorState.Forgotten
                && r.CampaignName == context.CampaignName
                && r.LastStateChangeDay <= archiveThresholdDay)
            .ToListAsync(ct);

        foreach (var rumor in forgottenRumors)
        {
            context.Session.Delete(rumor);
        }

        if (forgottenRumors.Count > 0)
        {
            narratives.Add(new RuleNarrative(
                $"[Engine] Archived {forgottenRumors.Count} rumors that were forgotten long ago.",
                Persist: false));
        }

        return new RuleResult(narratives, []);
    }
}
