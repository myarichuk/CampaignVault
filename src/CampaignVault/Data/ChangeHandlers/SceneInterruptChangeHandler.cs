using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class SceneInterruptChangeHandler : IWorldChangeHandler
{
    private readonly EncounterResolver _resolver;

    public SceneInterruptChangeHandler(EncounterResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public bool ShouldHandle(WorldChange change) => change is SceneInterruptCheck;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var sic = (SceneInterruptCheck)change;

        if (string.IsNullOrWhiteSpace(sic.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (string.IsNullOrWhiteSpace(sic.LocationId))
        {
            return ChangeHandlerResult.Failure("locationId is required.");
        }

        if (!ctx.Characters.TryGetValue(sic.CharacterId, out var character))
        {
            var suggested = await ctx.SuggestCharacterMatchAsync(sic.CharacterId);
            return ChangeHandlerResult.Failure(
                $"Character {sic.CharacterId} not found."
                + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (!ctx.Locations.TryGetValue(sic.LocationId, out var location))
        {
            var suggested = await ctx.SuggestLocationMatchAsync(sic.LocationId);
            return ChangeHandlerResult.Failure(
                $"Location {sic.LocationId} not found."
                + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (ctx.ActiveCombat != null)
        {
            return ChangeHandlerResult.Failure(
                "Scene interrupt check cannot run during active combat. Use combat promotion instead.");
        }

        if (!string.Equals(character.CurrentLocationId, sic.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            return ChangeHandlerResult.Failure(
                $"Character {character.Name} is not at {location.Name} (current: {character.CurrentLocationId ?? "unknown"}).");
        }

        // ctx.Characters is only batch-preloaded from this change's own CharacterId/LocationId
        // properties (the default reflection-based ExtractInvolvedEntities), so for a standalone
        // scene_interrupt_check it contains just the acting PC — union it with a direct location query
        // so NPCs present but not otherwise named in this batch still count.
        var presentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ctx.Characters.Values)
        {
            if (string.Equals(c.CurrentLocationId, sic.LocationId, StringComparison.OrdinalIgnoreCase))
            {
                presentIds.Add(c.Id);
            }
        }

        if (ctx.Session != null)
        {
            var presentNpcs = await PressureQueryHelper.QueryPresentNpcsAsync(ctx.Session, sic.LocationId, ct);
            foreach (var npc in presentNpcs)
            {
                presentIds.Add(npc.Id);
            }
        }

        presentIds.Remove(sic.CharacterId);
        var presentNpcCount = presentIds.Count;

        var hasCrowdContext = !string.IsNullOrWhiteSpace(location.AmbientCrowd)
                              || presentNpcCount >= 3;

        if (!hasCrowdContext)
        {
            return ChangeHandlerResult.Failure(
                "Scene interrupt requires ambientCrowd on the location or at least 3 other NPCs present. "
                + "Set ambientCrowd via location_update first.");
        }

        var time = await ctx.GetCurrentTimeAsync();
        var currentDay = (int)time.TotalDaysElapsed;

        if (ctx.Session != null
            && await PressureQueryHelper.HasSceneInterruptTodayAsync(
                ctx.Session, ctx.CampaignName, sic.LocationId, currentDay, ct))
        {
            return ChangeHandlerResult.Failure(
                $"Scene interrupt cooldown active for {location.Name} today (day {currentDay}). "
                + "Resolve the prior interrupt or wait until the next day.");
        }

        var heldItems = ctx.Items.Values
            .Where(i => string.Equals(i.HolderId, character.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var personalScore = SceneVulnerabilityHeuristics.ScoreCharacter(character, heldItems);
        var riskModifier = SceneVulnerabilityHeuristics.ResolveRiskModifier(sic.RiskModifier, personalScore);
        var contextModifier = SceneVulnerabilityHeuristics.ScoreLocationInterruptContext(
            location, presentNpcCount, ctx.Factions);

        var (interrupted, deltas, narratives) = await _resolver.EvaluateSceneInterruptAsync(
            ctx,
            character,
            location,
            riskModifier,
            contextModifier,
            sic.Notes);

        foreach (var delta in deltas)
        {
            await ctx.Dispatcher.DispatchMutationAsync(ctx, delta, ct);
        }

        if (!interrupted)
        {
            return new ChangeHandlerResult(
                true,
                $"Crowd interrupt check: no reaction this beat (riskModifier {riskModifier}, ctx +{contextModifier}).");
        }

        return new ChangeHandlerResult(
            true,
            $"Crowd INTERRUPT at {location.Name}! {string.Join(" ", narratives)} "
            + "One figure promoted from ambientCrowd — resolve before continuing.");
    }
}