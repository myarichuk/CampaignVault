using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Gently reminds the LLM to materialize Points of Interest when they become important.
/// The LLM (not keyword heuristics) decides whether an interaction, spell effect, combat impact,
/// reading, or other action has made a PoI's state worth persisting as recallable world data.
/// 
/// Examples of LLM-driven materialization:
/// - Reading the notice board → attach the specific posters as poiDetails.
/// - Firebolt hits the wall → add or update "scorch mark on the north wall" with details.
/// - Detect magic or light spell reveals hidden runes on a "strange obelisk" PoI.
/// - Player leans their axe on the bar → the bar PoI can get "recently used as a weapon rack" state.
/// 
/// This is analogous to deciding to promote a specific NPC from ambientCrowd, but for environment features.
/// </summary>
public sealed class PointOfInterestPressureContributor : IPressureContributor
{
    public const string UnmaterializedPoiGroupingKey = "Poi:Unmaterialized";
    public const string HasPoisGroupingKey = "Poi:HasLightOnly";

    public PressureScope Scope => PressureScope.Both;
    public int Order => 27;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (ctx.Scene is not { IsLocationAnchored: true })
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);

        var loc = ctx.Scene.Location;
        var unmaterialized = PointOfInterestHeuristics.GetUnmaterializedPois(loc);

        var hasRecentActivity = (ctx.Scene.RecentEvents?.Any() ?? false) || ctx.PartyPresent;

        // Time decay / evolution suggestions
        if (ctx.DaysAdvanced is > 0)
        {
            var hasDetailedPois = (loc.PointOfInterestDetails?.Count ?? 0) > 0;
            if (hasDetailedPois)
            {
                // Pick an example PoI that has details to suggest evolving it
                var examplePoi = loc.PointOfInterestDetails!.Keys.FirstOrDefault() ?? (loc.PointsOfInterest?.FirstOrDefault() ?? "a PoI");
                PointOfInterestHeuristics.TryBuildMaterializeExample(loc.Id, examplePoi, out var ex);

                pressures.Add(new WorldPressureItem(
                    PressureSeverity.Suggestion,
                    loc.Id,
                    $"Time advanced {ctx.DaysAdvanced} day(s). PoIs may have naturally changed (tavern cleaned after a brawl, scorch marks scrubbed, posters replaced or faded, temporary marks repaired). " +
                    "Consider updating or removing outdated poiDetails via location_update. Example of refreshing a PoI state:\n" + ex,
                    UnmaterializedPoiGroupingKey));
            }
        }

        if (unmaterialized.Count == 0)
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);

        // Mild suggestion whenever there are unmaterialized PoIs and the scene has seen activity.
        // The LLM chooses whether the current beat actually warrants materializing any of them.
        if (hasRecentActivity)
        {
            // Pick one for the example (first unmaterialized)
            var examplePoi = unmaterialized.First();
            PointOfInterestHeuristics.TryBuildMaterializeExample(loc.Id, examplePoi, out var example);

            var list = string.Join(", ", unmaterialized.Take(5).Select(p => $"\"{p}\""));

            pressures.Add(new WorldPressureItem(
                PressureSeverity.Suggestion,
                loc.Id,
                $"SUGGESTION: This location has PointsOfInterest without materialized details: [{list}]. " +
                "If any player action, spell, examination, or environmental change this beat made one of them important or revealed specific persistent information, materialize it so it is recallable on future visits. " +
                "Use location_update with materializePointOfInterest + poiDetails (the LLM decides relevance — e.g. a glow spell on a rune-covered pillar, a firebolt creating a scorch mark, reading wanted posters, etc.). Example:\n" + example,
                UnmaterializedPoiGroupingKey));
        }
        else if (unmaterialized.Count >= 3)
        {
            // Very light nudge for locations that have lots of flavor PoIs that have never been detailed
            PointOfInterestHeuristics.TryBuildMaterializeExample(loc.Id, unmaterialized[0], out var ex);
            pressures.Add(new WorldPressureItem(
                PressureSeverity.Suggestion,
                loc.Id,
                "SUGGESTION: Several PointsOfInterest exist but none have been materialized with details yet. When a PoI becomes relevant through play, materialize the discovered state using the location_update pattern above.",
                HasPoisGroupingKey));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}
