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
        ChangeContext context,
        CancellationToken ct = default)
    {
        var sic = (SceneInterruptCheck)change;

        if (string.IsNullOrWhiteSpace(sic.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (string.IsNullOrWhiteSpace(sic.LocationId))
        {
            return ChangeHandlerResult.Failure("locationId is required.");
        }

        if (!context.Characters.TryGetValue(sic.CharacterId, out var character))
        {
            var suggested = await context.SuggestCharacterMatchAsync(sic.CharacterId);
            return ChangeHandlerResult.Failure(
                $"Character {sic.CharacterId} not found."
                + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (!context.Locations.TryGetValue(sic.LocationId, out var location))
        {
            var suggested = await context.SuggestLocationMatchAsync(sic.LocationId);
            return ChangeHandlerResult.Failure(
                $"Location {sic.LocationId} not found."
                + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (context.ActiveCombat != null)
        {
            return ChangeHandlerResult.Failure(
                "Scene interrupt check cannot run during active combat. Use combat promotion instead.");
        }

        if (!string.Equals(character.CurrentLocationId, sic.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            return ChangeHandlerResult.Failure(
                $"Character {character.Name} is not at {location.Name} (current: {character.CurrentLocationId ?? "unknown"}).");
        }

        var presentNpcCount = context.Characters.Values.Count(c =>
            string.Equals(c.CurrentLocationId, sic.LocationId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.Id, sic.CharacterId, StringComparison.OrdinalIgnoreCase));

        var hasCrowdContext = !string.IsNullOrWhiteSpace(location.AmbientCrowd)
                              || presentNpcCount >= 3;

        if (!hasCrowdContext)
        {
            return ChangeHandlerResult.Failure(
                "Scene interrupt requires ambientCrowd on the location or at least 3 other NPCs present. "
                + "Set ambientCrowd via location_update first.");
        }

        var time = await context.GetCurrentTimeAsync();
        var currentDay = (int)time.TotalDaysElapsed;

        if (context.Session != null
            && await PressureQueryHelper.HasSceneInterruptTodayAsync(
                context.Session, context.CampaignName, sic.LocationId, currentDay, ct))
        {
            return ChangeHandlerResult.Failure(
                $"Scene interrupt cooldown active for {location.Name} today (day {currentDay}). "
                + "Resolve the prior interrupt or wait until the next day.");
        }

        var heldItems = context.Items.Values
            .Where(i => string.Equals(i.HolderId, character.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var personalScore = SceneVulnerabilityHeuristics.ScoreCharacter(character, heldItems);
        var riskModifier = SceneVulnerabilityHeuristics.ResolveRiskModifier(sic.RiskModifier, personalScore);
        var contextModifier = SceneVulnerabilityHeuristics.ScoreLocationInterruptContext(
            location, presentNpcCount, context.Factions);

        var (interrupted, deltas, narratives) = await _resolver.EvaluateSceneInterruptAsync(
            context,
            character,
            location,
            riskModifier,
            contextModifier,
            sic.Notes);

        foreach (var delta in deltas)
        {
            await context.Dispatcher.DispatchMutationAsync(context, delta, ct);
        }

        if (!interrupted)
        {
            return new ChangeHandlerResult(
                true,
                $"Crowd interrupt check: no reaction this beat (riskModifier {riskModifier}, context +{contextModifier}).");
        }

        return new ChangeHandlerResult(
            true,
            $"Crowd INTERRUPT at {location.Name}! {string.Join(" ", narratives)} "
            + "One figure promoted from ambientCrowd — resolve before continuing.");
    }
}