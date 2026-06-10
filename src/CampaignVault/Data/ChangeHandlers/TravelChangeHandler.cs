using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class TravelChangeHandler : IWorldChangeHandler
{
    private readonly EncounterResolver _resolver;

    public TravelChangeHandler() : this(new EncounterResolver()) { }

    public TravelChangeHandler(EncounterResolver resolver)
    {
        _resolver = resolver ?? new EncounterResolver();
    }

    public bool ShouldHandle(WorldChange change) => change is TravelChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var tc = (TravelChange)change;

        if (!context.Characters.TryGetValue(tc.CharacterId, out var character))
        {
            var suggested = await context.SuggestCharacterMatchAsync(tc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {tc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (character.SystemStats?.SpatialRelations != null)
        {
            var blocks = character.SystemStats.SpatialRelations
                .Where(r => r.RelationType == "GrappledBy" || r.RelationType == "Grappling" || r.RelationType == "LeaningIn")
                .ToList();
            if (blocks.Any())
            {
                var block = blocks.First();
                return ChangeHandlerResult.Failure($"Character {character.Name} cannot travel because they have a spatial relation '{block.RelationType}' with character {block.TargetId}. Resolve this relationship first.");
            }
        }

        if (!context.Locations.TryGetValue(tc.DestinationLocationId, out var destination))
        {
            var suggested = await context.SuggestLocationMatchAsync(tc.DestinationLocationId);
            return ChangeHandlerResult.Failure($"Destination location {tc.DestinationLocationId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        var time = await context.GetCurrentTimeAsync();

        // 3. Time & Need costs based on distance
        var totalHours = tc.TravelCostHoursOverride ?? 4; // Fallback default until exit metadata is available
        var terrain = tc.TerrainOverride;
        var encounterRiskModifier = tc.EncounterRiskModifier ?? 0;

        // 2. Lookup exit metadata if we have the start location
        if (character.CurrentLocationId != null)
        {
            if (!context.Locations.TryGetValue(character.CurrentLocationId, out var startLoc) || startLoc == null)
            {
                startLoc = await context.Session.LoadAsync<Location>(character.CurrentLocationId);
            }

            if (startLoc != null)
            {
                var exit = startLoc.Exits?.FirstOrDefault(e => e.TargetLocationId == tc.DestinationLocationId);
                if (exit != null)
                {
                    if (tc.TravelCostHoursOverride == null && exit.TravelCostHours.HasValue && exit.TravelCostHours.Value > 0)
                    {
                        totalHours = exit.TravelCostHours.Value;
                    }

                    if (tc.TerrainOverride == null)
                    {
                        terrain = exit.Terrain;
                    }
                }
            }
        }

        var (interrupted, hoursTraveled, deltas, narratives) = await _resolver.EvaluateAsync(
            context,
            character, 
            destination, 
            totalHours, 
            6, // bucket size 6 hours
            encounterRiskModifier,
            "Travel",
            terrain);

        // Apply partial time costs
        if (hoursTraveled > 0)
        {
            time.AdvanceHours(hoursTraveled);

            var tirednessDelta = (hoursTraveled / 4.0f) * 10f;
            if (tirednessDelta > 0)
            {
                await context.Dispatcher.DispatchMutationAsync(context, new NeedChange
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
            await context.Dispatcher.DispatchMutationAsync(context, delta, ct);
        }

        // 1. Update location & activity
        if (!interrupted)
        {
            await context.Dispatcher.DispatchMutationAsync(context, new ActivityChange
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
            
            context.RecordMessage($"Travel: {character.Name} traveled to {destination.Name}. {tc.Narrative}");
        }
        else
        {
            context.RecordMessage($"Travel interrupted: {string.Join(" ", narratives)}");
        }

        return ChangeHandlerResult.Ok;
    }
}
