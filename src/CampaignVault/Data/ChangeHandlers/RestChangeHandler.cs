using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class RestChangeHandler : IWorldChangeHandler
{
    private readonly EncounterResolver _resolver;

    public RestChangeHandler() : this(new EncounterResolver()) { }

    public RestChangeHandler(EncounterResolver resolver)
    {
        _resolver = resolver ?? new EncounterResolver();
    }

    public bool ShouldHandle(WorldChange change) => change is RestChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var rc = (RestChange)change;

        if (string.IsNullOrWhiteSpace(rc.CharacterId))
        {
            return ChangeHandlerResult.Failure("CharacterId is required.");
        }

        if (!context.Characters.TryGetValue(rc.CharacterId, out var character))
        {
            var suggested = await context.SuggestCharacterMatchAsync(rc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {rc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (string.IsNullOrWhiteSpace(rc.LocationId))
        {
            return ChangeHandlerResult.Failure("LocationId is required.");
        }

        if (!context.Locations.TryGetValue(rc.LocationId, out var location))
        {
            var suggested = await context.SuggestLocationMatchAsync(rc.LocationId);
            return ChangeHandlerResult.Failure($"Location {rc.LocationId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        var time = await context.GetCurrentTimeAsync();

        var (interrupted, hoursRested, deltas, narratives) = await _resolver.EvaluateAsync(
            context,
            character, 
            location, 
            rc.IntendedHours > 0 ? rc.IntendedHours : 8, 
            4, // bucket size 4 hours
            rc.SecurityModifier,
            "Rest");

        // Advance time
        if (hoursRested > 0)
        {
            time.AdvanceHours(hoursRested);
        }

        // Dispatch encounter events / transient NPCs
        foreach (var delta in deltas)
        {
            await context.Dispatcher.DispatchMutationAsync(context, delta, ct);
        }

        if (!interrupted)
        {
            await context.Dispatcher.DispatchMutationAsync(context, new ActivityChange
            {
                CharacterId = rc.CharacterId,
                NewLocationId = rc.LocationId,
                UpdateLocation = false,
                NewActivity = rc.NarrativeNote ?? "Rested peacefully.",
                Reason = "Rest complete"
            }, ct);

            return new ChangeHandlerResult(true, $"Rest completed safely. {hoursRested} hours passed.");
        }
        else
        {
            return new ChangeHandlerResult(true, $"Rest INTERRUPTED after {hoursRested} hours! Encounter spawned. Do NOT apply healing commits yet; resolve the encounter first.");
        }
    }
}
