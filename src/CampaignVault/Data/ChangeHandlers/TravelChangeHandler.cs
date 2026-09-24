using CampaignVault.Events;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class TravelChangeHandler : IWorldChangeHandler
{
    private readonly EncounterResolver _resolver;

    public TravelChangeHandler(EncounterResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public bool ShouldHandle(WorldChange change) => change is TravelChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var tc = (TravelChange)change;

        if (!ctx.Characters.TryGetValue(tc.CharacterId, out var character))
        {
            var suggested = await ctx.SuggestCharacterMatchAsync(tc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {tc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (character.SystemStats?.EngagementRelations != null)
        {
            var blocks = character.SystemStats.EngagementRelations
                .Where(EngagementRelationCatalog.BlocksTravel)
                .ToList();
            if (blocks.Any())
            {
                var block = blocks.First();
                return ChangeHandlerResult.Failure($"Character {character.Name} cannot travel because they are {block.Verb} with character {block.TargetId}. Resolve this engagement first.");
            }
        }

        if (!ctx.Locations.TryGetValue(tc.DestinationLocationId, out var destination))
        {
            var suggested = await ctx.SuggestLocationMatchAsync(tc.DestinationLocationId);
            return ChangeHandlerResult.Failure($"Destination location {tc.DestinationLocationId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        var time = await ctx.GetCurrentTimeAsync();
        var fromLocationId = character.CurrentLocationId;

        var terrain = tc.TerrainOverride;
        var encounterRiskModifier = tc.EncounterRiskModifier ?? 0;

        LocationExit? exit = null;
        if (character.CurrentLocationId != null)
        {
            if (!ctx.Locations.TryGetValue(character.CurrentLocationId, out var startLoc) || startLoc == null)
            {
                startLoc = await ctx.Session.LoadAsync<Location>(character.CurrentLocationId);
            }

            if (startLoc != null)
            {
                exit = startLoc.Exits?.FirstOrDefault(e => e.TargetLocationId == tc.DestinationLocationId);
                if (exit != null && tc.TerrainOverride == null)
                {
                    terrain = exit.Terrain;
                }
            }
        }

        double totalHours;
        if (tc.TravelCostHoursOverride != null)
        {
            totalHours = tc.TravelCostHoursOverride.Value;
        }
        else if (exit?.TravelCostHours is > 0)
        {
            totalHours = exit.TravelCostHours.Value;
        }
        else if (exit != null)
        {
            totalHours = 4;
        }
        else
        {
            var origin = character.CurrentLocationId ?? "(unknown origin)";
            return ChangeHandlerResult.Failure(
                $"No LocationExit from {origin} to {tc.DestinationLocationId}, and travelCostHoursOverride was not supplied. Add an exit on the origin, or pass travelCostHoursOverride.");
        }

        var (interrupted, hoursTraveled, deltas, narratives) = await _resolver.EvaluateAsync(
            ctx,
            character,
            destination,
            totalHours,
            6, // bucket size 6 hours
            encounterRiskModifier,
            "Travel",
            terrain,
            spawnLocationId: character.CurrentLocationId);

        // Apply partial time costs
        if (hoursTraveled > 0)
        {
            time.AdvanceHours(hoursTraveled);

            // Travel marches at a higher tiredness rate than ordinary ambient decay. Dispatched
            // directly here (rather than left to the day-tick's ambient sweep) so travel keeps its
            // own distinct pace; CampaignRepository.StageChangesAsync marks this character exempt
            // from the ambient tick's tiredness accrual for this commit so it isn't double-applied.
            // Hunger/thirst/social_drive are NOT handled here — the day-tick that now reliably fires
            // right after this commit (via CampaignTime.UnsimulatedHours) already accrues those at the
            // ordinary ambient rate for every character, this one included.
            var tirednessDelta = (float)((hoursTraveled / 4.0) * 10.0);
            if (tirednessDelta > 0)
            {
                await ctx.Dispatcher.DispatchMutationAsync(ctx, new NeedChange
                {
                    CharacterId = tc.CharacterId,
                    Need = "tiredness",
                    Delta = tirednessDelta
                }, ct);
            }
        }

        // Apply generated deltas from the rule (e.g. ActivityChange if interrupted, EventOccurred)
        foreach (var delta in deltas)
        {
            await ctx.Dispatcher.DispatchMutationAsync(ctx, delta, ct);
        }

        // 1. Update location & activity
        if (!interrupted)
        {
            await ctx.Dispatcher.DispatchMutationAsync(ctx, new ActivityChange
            {
                CharacterId = tc.CharacterId,
                NewLocationId = tc.DestinationLocationId,
                UpdateLocation = true,
                NewActivity = tc.Narrative ?? "Traveling",
                Reason = "Travel complete"
            }, ct);

            // Mark destination as visited only if we actually arrived
            destination.LastVisitedDay = (int)time.TotalDaysElapsed;
            destination.LastUpdated = DateTime.UtcNow;

            // A Region is the broadest tier (see LocationType) and typically carries a wide scope of
            // attached quests/NPCs/rumors. Landing a character's exact CurrentLocationId directly on one
            // — instead of a child Location created for the specific spot (a campsite, a clearing, a
            // hiding place) — pulls that whole scope into every subsequent get_scene/get_world_state/
            // take_turn call for a beat that was really about one small patch of it. Advisory only: never
            // blocks the travel, since sometimes the region-level node genuinely is the destination.
            if (destination.Type == LocationType.Region)
            {
                ctx.RecordMessage(
                    $"NOTE: {destination.Name} ({destination.Id}) is a broad Region. If this stop is a specific spot " +
                    "within it rather than the whole region, consider creating a child Location first (world_build " +
                    $"with parentLocationId='{destination.Id}') and traveling there instead — otherwise this scene inherits " +
                    "the entire region's quests/NPCs/rumors.");
            }

            await ClearStaleEngagementsAsync(character, tc.DestinationLocationId, ctx, ct);

            var msg = $"Travel: {character.Name} traveled to {destination.Name}. {tc.Narrative}";
            await ctx.Dispatcher.DispatchMutationAsync(ctx, new EventOccurred
            {
                Category = EventCategory.Travel,
                Summary = msg,
                Involved = [character.Id],
                LocationId = destination.Id,
                Details = new Dictionary<string, object> { ["hoursTraveled"] = hoursTraveled }
            }, ct);

            ctx.Publish(CoreEvents.Traveled, new Dictionary<string, object?>
            {
                [CoreEvents.Fields.CharacterId] = character.Id,
                [CoreEvents.Fields.FromLocationId] = fromLocationId,
                [CoreEvents.Fields.LocationId] = destination.Id,
                [CoreEvents.Fields.Hours] = hoursTraveled
            });
        }
        else
        {
            ctx.RecordMessage($"Travel interrupted: {string.Join(" ", narratives)}");

            await ctx.Dispatcher.DispatchMutationAsync(ctx, new EventOccurred
            {
                Category = EventCategory.Travel,
                Summary = $"Travel interrupted: {character.Name} did not reach {destination.Name}. {string.Join(" ", narratives)}".Trim(),
                Involved = [character.Id],
                LocationId = character.CurrentLocationId,
                Details = new Dictionary<string, object> { ["hoursTraveled"] = hoursTraveled }
            }, ct);
        }

        return ChangeHandlerResult.Ok;
    }

    /// <summary>
    /// Clears engagement relations left over from the departure location. Any relation still on the
    /// character at this point is guaranteed non-Hard (Hard relations already blocked travel above),
    /// so this only ever resolves Social/Attention/Proximity engagements — conversations, being watched,
    /// standing close to someone — that no longer make sense once the character has left. A relation is
    /// kept only if its target ends up at the same destination (i.e. they traveled together).
    /// </summary>
    private static async Task ClearStaleEngagementsAsync(
        Character character, string destinationLocationId, IChangeContext context, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        var relations = character.SystemStats?.EngagementRelations;
        if (relations is not { Count: > 0 })
        {
            return;
        }

        foreach (var relation in relations.ToList())
        {
            if (HasCoTravelInBatch(relation.TargetId, destinationLocationId, context))
            {
                continue;
            }

            if (!context.Characters.TryGetValue(relation.TargetId, out var target))
            {
                target = ctx.Session != null
                    ? await ctx.Session.LoadAsync<Character>(relation.TargetId, ct)
                    : null;
            }

            if (target?.CurrentLocationId == destinationLocationId)
            {
                continue;
            }

            if (target != null)
            {
                await ctx.Dispatcher.DispatchMutationAsync(ctx, new EngagementRelationChange
                {
                    CharacterId = character.Id,
                    TargetId = relation.TargetId,
                    Verb = null,
                    Bidirectional = true
                }, ct);
            }
            else
            {
                character.SystemStats!.EngagementRelations.RemoveAll(r => r.TargetId == relation.TargetId);
            }
        }
    }

    /// <summary>
    /// True if the relation's target has its own TravelChange to the same destination somewhere in this
    /// commit batch. A whole party traveling together is normally expressed as one TravelChange per
    /// character in the same take_turn batch — without this check, whoever's TravelChange happens to be
    /// processed first would see their companions still parked at the origin (the companions' own
    /// TravelChange hasn't run yet) and sever the relation, even though everyone is headed to the same
    /// place in the same beat. Checking the batch directly makes the outcome order-independent.
    /// </summary>
    private static bool HasCoTravelInBatch(string targetId, string destinationLocationId, IChangeContext context) =>
        context.Batch?.OfType<TravelChange>().Any(tc =>
            string.Equals(tc.CharacterId, targetId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tc.DestinationLocationId, destinationLocationId, StringComparison.OrdinalIgnoreCase)) == true;

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not TravelChange tc) return false;

        if (!string.IsNullOrEmpty(tc.CharacterId))
        {
            characterIds?.Add(tc.CharacterId);
            allInvolvedIds?.Add(tc.CharacterId);
            // Note: We cannot pre-extract the origin location here because we don't have the character loaded yet
            // to check character.CurrentLocationId. This must be handled by the dispatcher's fallback or 
            // by a subsequent context load inside the handler.
        }

        if (!string.IsNullOrEmpty(tc.DestinationLocationId))
        {
            locationIds?.Add(tc.DestinationLocationId);
            allInvolvedIds?.Add(tc.DestinationLocationId);
        }

        return true;
    }
}
