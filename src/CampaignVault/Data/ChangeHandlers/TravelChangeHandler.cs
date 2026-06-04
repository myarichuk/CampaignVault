using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class TravelChangeHandler : IWorldChangeHandler
{
    private readonly TravelEncounterRule _travelRule;

    public TravelChangeHandler() : this(new TravelEncounterRule()) { }

    /// <summary>
    /// Test seam: allows injecting a TravelEncounterRule with a controlled random source
    /// (e.g. deterministic Func<double>) so interrupt behavior can be forced reliably in tests.
    /// </summary>
    public TravelChangeHandler(TravelEncounterRule travelRule)
    {
        _travelRule = travelRule ?? new TravelEncounterRule();
    }

    public bool ShouldHandle(WorldChange change) => change is TravelChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, System.Threading.CancellationToken ct = default)
    {
        var tc = (TravelChange)change;

        if (!context.Characters.TryGetValue(tc.CharacterId, out var character))
        {
            var suggested = await context.SuggestCharacterMatchAsync(tc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {tc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (!context.Locations.TryGetValue(tc.DestinationLocationId, out var destination))
        {
            var suggested = await context.SuggestLocationMatchAsync(tc.DestinationLocationId);
            return ChangeHandlerResult.Failure($"Destination location {tc.DestinationLocationId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        var time = await context.GetCurrentTimeAsync();

        // 3. Time & Need costs based on distance
        int totalHours = tc.TravelCostHoursOverride ?? 4; // Fallback default until exit metadata is available
        string? terrain = tc.TerrainOverride;
        int encounterRiskModifier = tc.EncounterRiskModifier ?? 0;

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
                    if (tc.TravelCostHoursOverride == null && exit.TravelCostHours.HasValue && exit.TravelCostHours.Value > 0) totalHours = exit.TravelCostHours.Value;
                    if (tc.TerrainOverride == null) terrain = exit.Terrain;
                }
            }
        }

        var (interrupted, hoursTraveled, deltas, narratives) = _travelRule.EvaluateTravel(character, destination, totalHours, terrain, encounterRiskModifier);

        // Apply partial time costs
        if (hoursTraveled > 0)
        {
            float tirednessDelta = (hoursTraveled / 4.0f) * 10f;
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
            destination.LastVisitedDay = time.TotalDaysElapsed;
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
