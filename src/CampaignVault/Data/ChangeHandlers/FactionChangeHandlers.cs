using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class FactionReputationChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is FactionReputationChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var frc = (FactionReputationChange)change;

        if (!ctx.Characters.TryGetValue(frc.CharacterId, out var character))
        {
            var suggested = await ctx.SuggestCharacterMatchAsync(frc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {frc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (!ctx.Factions.TryGetValue(frc.FactionId, out var faction))
        {
            var suggested = await ctx.SuggestFactionMatchAsync(frc.FactionId);
            return ChangeHandlerResult.Failure($"Faction {frc.FactionId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        character.Social ??= new();
        character.Social.FactionReputations ??= new();

        if (character.Social.FactionReputations.ContainsKey(frc.FactionId))
        {
            character.Social.FactionReputations[frc.FactionId] = Math.Clamp(character.Social.FactionReputations[frc.FactionId] + frc.Delta, -100, 100);
        }
        else
        {
            character.Social.FactionReputations[frc.FactionId] = Math.Clamp(frc.Delta, -100, 100);
        }

        character.LastUpdated = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(frc.Reason))
        {
            await ctx.Dispatcher.DispatchMutationAsync(ctx, new EventOccurred
            {
                Category = EventCategory.Interaction,
                Summary = $"Reputation with {faction.Name} changed. {frc.Reason}",
                Involved = [frc.CharacterId]
            }, ct);
        }

        ctx.RecordMessage($"Reputation for {character.Name} with {faction.Name} changed by {frc.Delta} to {character.Social.FactionReputations[frc.FactionId]}.");

        return ChangeHandlerResult.Ok;
    }
}

public class FactionStateChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is FactionStateChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var fsc = (FactionStateChange)change;

        if (!ctx.Factions.TryGetValue(fsc.FactionId, out var faction))
        {
            var suggested = await ctx.SuggestFactionMatchAsync(fsc.FactionId);
            return ChangeHandlerResult.Failure($"Faction {fsc.FactionId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (fsc.NewStance == null && !fsc.InfluenceDelta.HasValue)
        {
            ctx.RecordMessage($"FactionStateChange for {fsc.FactionId}: no stance or influence delta specified — no changes made.");
            return ChangeHandlerResult.Ok;
        }

        if (fsc.NewStance != null)
        {
            if (string.IsNullOrWhiteSpace(fsc.TargetFactionId))
            {
                return ChangeHandlerResult.Failure(
                    "newStance was set but targetFactionId is missing — stance changes require a target faction.");
            }

            faction.StanceToward ??= new();
            faction.StanceToward[fsc.TargetFactionId] = fsc.NewStance.Value;
        }

        if (fsc.InfluenceDelta.HasValue)
        {
            faction.InfluenceLevel = Math.Clamp(faction.InfluenceLevel + fsc.InfluenceDelta.Value, 0, 100);
        }

        faction.LastUpdated = DateTime.UtcNow;

        if (fsc.InfluenceDelta.HasValue)
        {
            ctx.RecordMessage($"{faction.Name}'s InfluenceLevel is now {faction.InfluenceLevel} (clamped 0-100).");
        }

        return ChangeHandlerResult.Ok;
    }
}
