using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Temporary hit points (<see cref="TempHpGrant"/>). One path for every source: temporary hit points don't stack, so a grant
/// replaces what the character has only when it is larger. <see cref="DamageApplication"/> spends them first.
/// </summary>
public sealed class TempHpGrantHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is TempHpGrant;

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not TempHpGrant grant || string.IsNullOrWhiteSpace(grant.CharacterId))
            return false;

        characterIds?.Add(grant.CharacterId);
        allInvolvedIds?.Add(grant.CharacterId);
        return true;
    }

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var grant = (TempHpGrant)change;

        if (string.IsNullOrWhiteSpace(grant.CharacterId))
            return ChangeHandlerResult.Failure("characterId is required.");
        if (grant.Amount <= 0)
            return ChangeHandlerResult.Failure("amount must be positive: temporary hit points are granted, never taken (damage spends them).");

        var id = grant.CharacterId.Trim();
        if (!ctx.Characters.TryGetValue(id, out var character))
        {
            if (ctx.Session is null || !id.StartsWith("chars/", StringComparison.Ordinal)
                || await ctx.Session.LoadAsync<Character>(id, ct) is not { } loaded)
                return ChangeHandlerResult.Failure($"{id} is not a known character.");
            ctx.RegisterNewCharacter(loaded);
            character = loaded;
        }

        if (character.IsDead)
            return ChangeHandlerResult.Failure($"{id} is dead; temporary hit points can't reach them.");

        var source = string.IsNullOrWhiteSpace(grant.Source) ? null : grant.Source.Trim();
        var label = source is null ? "" : $" from {source}";
        if (character.TempHp is { } current && current >= grant.Amount)
        {
            ctx.RecordMessage(
                $"{character.Name} keeps their {current} temporary HP{(character.TempHpSource is { } had ? $" ({had})" : "")}: "
                + $"{grant.Amount}{label} isn't more, and temporary hit points don't stack.");
            return ChangeHandlerResult.Ok;
        }

        character.TempHp = grant.Amount;
        character.TempHpSource = source;
        ctx.RecordMessage($"{character.Name} gains {grant.Amount} temporary HP{label}.");
        return ChangeHandlerResult.Ok;
    }
}
