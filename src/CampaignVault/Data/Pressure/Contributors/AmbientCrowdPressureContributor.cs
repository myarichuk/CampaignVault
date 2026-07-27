using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Nudges the LLM to promote individuals from ambientCrowd when scenes are sparse or recent beats
/// imply an interactable figure who is not yet anchored. Also reminds to refresh crowd flavor after time passes.
/// </summary>
public sealed class AmbientCrowdPressureContributor : IPressureContributor
{
    public const string SparseCrowdGroupingKey = "AmbientCrowd:Sparse";
    public const string UnanchoredBeatGroupingKey = "AmbientCrowd:UnanchoredBeat";
    public const string DynamicRefreshGroupingKey = "AmbientCrowd:DynamicRefresh";

    public PressureScope Scope => PressureScope.Both;
    public int Order => 26;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (ctx.Scene is { IsLocationAnchored: true })
        {
            pressures.AddRange(EvaluateScene(ctx.Scene));
        }

        if (ctx.DaysAdvanced is > 0)
        {
            pressures.AddRange(await EvaluateWorldAfterTimePassAsync(ctx, ct));
        }

        return pressures;
    }

    private static IEnumerable<WorldPressureItem> EvaluateScene(SceneView scene)
    {
        var loc = scene.Location;
        if (loc.PointsOfInterest.Count == 0 && string.IsNullOrWhiteSpace(loc.AmbientCrowd))
        {
            AmbientCrowdHeuristics.TryBuildAmbientPopulateExample(loc.Id, loc.AmbientCrowd, out var example);

            yield return new WorldPressureItem(
                PressureSeverity.Suggestion,
                loc.Id,
                $"SUGGESTION: Location may narratively require ambient crowd but {nameof(loc.AmbientCrowd)} property is null or empty. "
                + "Example:\n" + example,
                SparseCrowdGroupingKey);
        }

        var presentCount = scene.PresentNPCs?.Count() ?? 0;
        var implied = AmbientCrowdHeuristics.EstimateImpliedCrowdSize(loc.AmbientCrowd);
        var dense = AmbientCrowdHeuristics.IsCrowdDenseEnough(loc.AmbientCrowd);

        if (dense && presentCount == 0)
        {
            AmbientCrowdHeuristics.TryBuildPromotionExample(loc.Id, out var example);
            yield return new WorldPressureItem(
                PressureSeverity.NarrativePrompt,
                loc.Id,
                $"NARRATIVE PROMPT: Location expects '{loc.AmbientCrowd}' but PresentNPCs is empty. "
                + "When someone from the crowd becomes interactable (approaches, speaks, picks up a weapon, offers a quest), "
                + "promote only that individual via world_build — not the whole crowd. Example:\n" + example,
                SparseCrowdGroupingKey);
        }
        else if (dense && implied >= 6 && presentCount < Math.Max(2, implied / 8))
        {
            AmbientCrowdHeuristics.TryBuildPromotionExample(loc.Id, out var example);
            yield return new WorldPressureItem(
                PressureSeverity.Suggestion,
                loc.Id,
                $"Ambient crowd '{loc.AmbientCrowd}' implies many people, but only {presentCount} NPC(s) are anchored here. "
                + "Keep bulk crowd as narration/ambientCrowd; when a specific person steps out (drunk, spear-bearer, merchant, witness), "
                + "promote just them:\n" + example,
                SparseCrowdGroupingKey);
        }

        var recent = scene.RecentEvents?.OrderByDescending(e => e.Timestamp).Take(5) ?? [];
        foreach (var ev in recent)
        {
            if (!AmbientCrowdHeuristics.EventImpliesUnanchoredBeat(ev, loc.Id))
            {
                continue;
            }

            AmbientCrowdHeuristics.TryBuildPromotionExample(loc.Id, out var example);
            yield return new WorldPressureItem(
                PressureSeverity.NarrativePrompt,
                loc.Id,
                $"NARRATIVE PROMPT: Recent scene activity ('{TrimSummary(ev.Summary)}') sounds like a specific person from the crowd, "
                + "but no matching character ID is involved. Promote that individual (hostile or friendly) before they speak, fight, or trade:\n"
                + example,
                UnanchoredBeatGroupingKey);
            break;
        }
    }

    private static async Task<IEnumerable<WorldPressureItem>> EvaluateWorldAfterTimePassAsync(
        PressureContext ctx,
        CancellationToken ct)
    {
        var pressures = new List<WorldPressureItem>();
        var currentDay = (int)ctx.Time.TotalDaysElapsed;
        var lookbackDays = Math.Max(1, ctx.DaysAdvanced ?? 1) + 2;

        var visited = await QueryRecentlyVisitedCrowdedLocationsAsync(
            ctx.Session, ctx.CampaignName, currentDay, lookbackDays, ct);

        foreach (var loc in visited.Take(3))
        {
            AmbientCrowdHeuristics.TryBuildAmbientRefreshExample(loc.Id, loc.AmbientCrowd, out var example);
            pressures.Add(new WorldPressureItem(
                PressureSeverity.Suggestion,
                loc.Id,
                $"Time advanced {ctx.DaysAdvanced} day(s). Party recently visited '{loc.Name}' "
                + $"(ambient: '{loc.AmbientCrowd}'). Consider whether the crowd mood shifted — update via location_update "
                + "or promote a new face if someone memorable should still be present:\n" + example,
                DynamicRefreshGroupingKey));
        }

        return pressures;
    }

    private static async Task<List<Location>> QueryRecentlyVisitedCrowdedLocationsAsync(
        IAsyncDocumentSession session,
        string campaignName,
        int currentDay,
        int lookbackDays,
        CancellationToken ct)
    {
        var minVisitedDay = Math.Max(0, currentDay - lookbackDays);
        var locations = await session.Query<Location>()
            .Where(l => (l.CampaignName == campaignName || l.CampaignName == null)
                        && l.AmbientCrowd != null
                        && l.LastVisitedDay != null
                        && l.LastVisitedDay >= minVisitedDay)
            .Take(20)
            .ToListAsync(ct);

        return locations
            .Where(l => !string.IsNullOrWhiteSpace(l.AmbientCrowd))
            .OrderByDescending(l => l.LastVisitedDay)
            .ToList();
    }

    private static string TrimSummary(string summary) =>
        summary.Length <= 80 ? summary : summary[..77] + "...";
}