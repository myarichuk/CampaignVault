using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class FactionCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is FactionCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var fc = (FactionCreate)change;

        if (string.IsNullOrWhiteSpace(fc.FactionId) || string.IsNullOrWhiteSpace(fc.Name))
        {
            return ChangeHandlerResult.Failure("FactionId and Name are required.");
        }

        if (context.Factions.ContainsKey(fc.FactionId))
        {
            return ChangeHandlerResult.Failure($"Faction {fc.FactionId} already exists.");
        }

        var faction = new Faction
        {
            Id = fc.FactionId,
            Name = fc.Name,
            Description = fc.Description,
            FactionType = fc.FactionType,
            ControllingTerritory = fc.ControllingTerritory,
            TerritoryLocationIds = fc.TerritoryLocationIds ?? [],
            KnownLeaderIds = fc.KnownLeaderIds ?? [],
            InfluenceLevel = fc.InitialInfluenceLevel ?? 50,
            CampaignName = context.CampaignName,
            LastUpdated = DateTime.UtcNow
        };

        context.RegisterNewFaction(faction);
        await context.Session.StoreAsync(faction, ct);
        context.RecordMessage($"Created new faction: {fc.Name}");

        return ChangeHandlerResult.Ok;
    }
}

public class FactionReputationChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is FactionReputationChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var frc = (FactionReputationChange)change;

        if (!context.Characters.TryGetValue(frc.CharacterId, out var character))
        {
            var suggested = await context.SuggestCharacterMatchAsync(frc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {frc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (!context.Factions.TryGetValue(frc.FactionId, out var faction))
        {
            var suggested = await context.SuggestFactionMatchAsync(frc.FactionId);
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
            await context.Dispatcher.DispatchMutationAsync(context, new EventOccurred
            {
                Category = EventCategory.Interaction,
                Summary = $"Reputation with {faction.Name} changed. {frc.Reason}",
                Involved = [frc.CharacterId, frc.FactionId]
            }, ct);
        }

        context.RecordMessage($"Reputation for {character.Name} with {faction.Name} changed by {frc.Delta} to {character.Social.FactionReputations[frc.FactionId]}.");

        return ChangeHandlerResult.Ok;
    }
}

public class FactionStateChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is FactionStateChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var fsc = (FactionStateChange)change;

        if (!context.Factions.TryGetValue(fsc.FactionId, out var faction))
        {
            var suggested = await context.SuggestFactionMatchAsync(fsc.FactionId);
            return ChangeHandlerResult.Failure($"Faction {fsc.FactionId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (fsc.NewStance == null && !fsc.InfluenceDelta.HasValue)
        {
            context.RecordMessage($"FactionStateChange for {fsc.FactionId}: no stance or influence delta specified — no changes made.");
            return ChangeHandlerResult.Ok;
        }

        if (fsc.NewStance != null && !string.IsNullOrWhiteSpace(fsc.TargetFactionId))
        {
            faction.StanceToward ??= new();
            faction.StanceToward[fsc.TargetFactionId] = fsc.NewStance.Value;
        }

        if (fsc.InfluenceDelta.HasValue)
        {
            faction.InfluenceLevel = Math.Clamp(faction.InfluenceLevel + fsc.InfluenceDelta.Value, 0, 100);
        }

        faction.LastUpdated = DateTime.UtcNow;

        context.RecordMessage($"Faction state changed for {faction.Name}. {fsc.Narrative}");

        return ChangeHandlerResult.Ok;
    }
}
