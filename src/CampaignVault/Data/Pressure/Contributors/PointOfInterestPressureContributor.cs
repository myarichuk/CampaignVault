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

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (ctx.Scene is not { IsLocationAnchored: true })
            return pressures;

        var loc = ctx.Scene.Location;
        var unmaterialized = PointOfInterestHeuristics.GetUnmaterializedPois(loc.PointsOfInterest, loc.PointOfInterestDetails);

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

        // A PoI that an ActivityChange has ever targeted (character placed/doing something there,
        // not just described) is functioning as a real place, regardless of how it was named —
        // unlike PointOfInterestHeuristics' sub-location suggestion below, which only fires for
        // PoI names that happen to look like an entrance/passage. One use is enough: if a character
        // was placed there, it's a place. Not gated on hasRecentActivity/DaysAdvanced — this reflects
        // persisted Location state (PoisUsedByActivity), so it resurfaces on any scene load for this
        // location regardless of whether the triggering ActivityChange happened this exact turn.
        var rawLocation = await ctx.Session.LoadAsync<Location>(loc.Id, ct);
        if (rawLocation?.PoisUsedByActivity is { Count: > 0 } usedPois)
        {
            var examplePoi = usedPois[0];
            var childId = $"locations/{Guid.NewGuid().ToString("N")[..8]}";
            var promoteExample =
                "{ \"locations\": [ { \"id\": \"" + childId + "\", \"name\": \"" + examplePoi.Replace("\"", "\\\"") + "\", " +
                "\"description\": \"...\", \"type\": \"Room\", \"parentLocationId\": \"" + loc.Id + "\", " +
                "\"connectedFromLocationId\": \"" + loc.Id + "\", \"connectionDescription\": \"" + examplePoi.Replace("\"", "\\\"") + "\" } ] }";

            var usedList = string.Join(", ", usedPois.Take(5).Select(p => $"\"{p}\""));

            // PresentNPCs is scoped to the whole parent location (CampaignRepository.cs's
            // GetSceneTargetIds is intentionally not sub-location-aware — see its own doc comment),
            // so any NPC present here is being shown as co-located with whoever's activity targeted
            // usedPois, even if they're narratively in a different room. That's not a hypothetical —
            // it's already wrong on the wire the instant both are true. Escalate past Suggestion so
            // it can't be quietly ignored for turns on end the way a Suggestion routinely is.
            var hasOtherPresentNpcs = ctx.Scene.PresentNPCs?.Any() ?? false;
            var severity = hasOtherPresentNpcs ? PressureSeverity.EngineWarning : PressureSeverity.Suggestion;
            var text = hasOtherPresentNpcs
                ? $"ENGINE WARNING: This location has no room-level granularity — PresentNPCs below is being shown as co-located with whoever's activity was placed at PoI(s) [{usedList}], even if they're narratively in a different room (e.g. one character asleep in a back room while others are elsewhere in the same building). " +
                  "Promote the PoI into a proper child Location via world_build before narrating anyone as separated from or rejoined with the group — otherwise \"who's in the room\" and what the engine reports will diverge. Example:\n" + promoteExample
                : $"SUGGESTION: PoI(s) [{usedList}] have had characters' activity placed there (updateLocation+poiName), not just described — that's real occupancy, not flavor text. " +
                  "Consider promoting via world_build into a proper child Location instead of continuing to narrate through poiDetails (which forces a full location resend and goes stale immediately). Example:\n" + promoteExample;

            pressures.Add(new WorldPressureItem(
                severity,
                loc.Id,
                text,
                "Poi:UsedAsSubLocation"));
        }

        if (unmaterialized.Count == 0)
            return pressures;

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

        return pressures;
    }
}
