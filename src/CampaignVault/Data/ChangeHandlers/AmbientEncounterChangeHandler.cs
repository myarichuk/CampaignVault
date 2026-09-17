using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Rolls EncounterResolver for elapsed ambient time at a location — the same mechanism RestChange/
/// TravelChange already use for their own spans, so advance_world/take_turn's ambient day-boundary
/// crossing gets the same encounter risk regardless of whether the LLM explicitly committed a
/// rest/travel change. See AmbientEncounterCheck for how this gets emitted.
/// </summary>
public class AmbientEncounterChangeHandler : IWorldChangeHandler
{
    private readonly EncounterResolver _resolver;

    public AmbientEncounterChangeHandler(EncounterResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public bool ShouldHandle(WorldChange change) => change is AmbientEncounterCheck;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var aec = (AmbientEncounterCheck)change;

        if (string.IsNullOrWhiteSpace(aec.LocationId) || aec.Hours <= 0)
        {
            return ChangeHandlerResult.Ok;
        }

        if (!context.Locations.TryGetValue(aec.LocationId, out var location)
            || context.Session == null
            || context.CampaignName == null)
        {
            // Engine-authored and best-effort: missing context shouldn't fail the whole tick.
            return ChangeHandlerResult.Ok;
        }

        // No CharacterId on this change (it's an ambient, party-wide check, not tied to one commit's
        // acting character) — resolve whichever PC is actually here to attribute the roll to.
        var partyHere = await PressureQueryHelper.QueryPartyAtLocationAsync(
            context.Session, context.CampaignName, aec.LocationId, 1, ct);
        var character = partyHere.FirstOrDefault();
        if (character == null)
        {
            return ChangeHandlerResult.Ok;
        }

        var (interrupted, _, deltas, narratives) = await _resolver.EvaluateAsync(
            context,
            character,
            location,
            aec.Hours,
            6, // bucket size — same granularity as travel
            0,
            "Rest"); // closest existing chance tier for extended idle/stationary time at a location

        foreach (var delta in deltas)
        {
            await context.Dispatcher.DispatchMutationAsync(context, delta, ct);
        }

        return interrupted
            ? new ChangeHandlerResult(true, $"Ambient encounter check at {location.Name}: interrupted after {aec.Hours:F0}h. {string.Join(" ", narratives)}")
            : ChangeHandlerResult.Ok;
    }
}
