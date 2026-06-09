using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class FactionRecentEventPressureContributor : IPressureContributor
{
    public const string PresenceChangeGroupingKey = "Faction:PresenceChange";
    public const string ReputationGroupingKey = "Faction:Reputation";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 65;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.RelevantFactions == null || !ctx.Scene.RelevantFactions.Any())
        {
            return pressures;
        }

        var fIds = ctx.Scene.RelevantFactions.Select(f => f.FactionId).ToList();
        var minDay = (int)ctx.Time.TotalDaysElapsed - 2;
        var recentEvents = await PressureQueryHelper.QueryRecentCampaignEventsAsync(ctx.Session, ctx.CampaignName, minDay, 50, ct);

        var simEvents = recentEvents.Where(e => e.Category == EventCategory.Simulation).ToList();
        var commitEvents = recentEvents.Where(e => e.Category == EventCategory.SceneCommit).ToList();

        foreach (var ev in simEvents)
        {
            if (ev.Involved == null || !ev.Involved.Any(id => fIds.Contains(id)))
            {
                continue;
            }

            var invFaction = ev.Involved.First(id => fIds.Contains(id));

            if (commitEvents.Any(c => c.Timestamp >= ev.Timestamp && c.Involved != null && c.Involved.Contains(invFaction)))
            {
                continue;
            }

            if (ev.Summary.Contains("influence", StringComparison.OrdinalIgnoreCase))
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, invFaction,
                    $"Faction '{invFaction}' recently expanded influence here. Update a local NPC's dialogue or create a rumor. Example:\n[ {{ \"$type\": \"event\", \"summary\": \"Reflected faction influence\", \"involved\": [\"{invFaction}\"] }} ]",
                    PresenceChangeGroupingKey));
            }
            else if (ev.Summary.Contains("Hostile") || ev.Summary.Contains("AtWar") || ev.Summary.Contains("war", StringComparison.OrdinalIgnoreCase))
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, invFaction,
                    $"Faction '{invFaction}' is involved in recent hostilities. Consider updating a local NPC's reputation to reflect their stance. Example:\n[ {{ \"$type\": \"faction_reputation\", \"characterId\": \"chars/local\", \"factionId\": \"{invFaction}\", \"delta\": -20 }} ]",
                    ReputationGroupingKey));
            }
        }

        return pressures;
    }
}