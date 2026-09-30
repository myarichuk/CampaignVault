using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// One 5e death saving throw for a player character at 0 HP (<see cref="DeathSaveChange"/>). Three failures kill via
/// <see cref="DeathChangeHandler"/>; three successes stabilise; a natural 20 wakes the character at 1 HP.
/// </summary>
public sealed class DeathSaveChangeHandler(IRollService rollService) : IWorldChangeHandler
{
    private readonly IRollService _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));

    public bool ShouldHandle(WorldChange change) => change is DeathSaveChange;

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not DeathSaveChange ds || string.IsNullOrWhiteSpace(ds.CharacterId))
            return false;

        characterIds?.Add(ds.CharacterId);
        allInvolvedIds?.Add(ds.CharacterId);
        return true;
    }

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var req = (DeathSaveChange)change;

        if (string.IsNullOrWhiteSpace(req.CharacterId))
            return ChangeHandlerResult.Failure("characterId is required.");
        if (req.Roll is < 1 or > 20)
            return ChangeHandlerResult.Failure("roll must be a d20 value from 1 to 20 (omit it to let the engine roll).");

        var id = req.CharacterId.Trim();
        if (!ctx.Characters.TryGetValue(id, out var character))
        {
            if (ctx.Session is null || !id.StartsWith("chars/", StringComparison.Ordinal)
                || await ctx.Session.LoadAsync<Character>(id, ct) is not { } loaded)
                return ChangeHandlerResult.Failure($"{id} is not a known character.");
            ctx.RegisterNewCharacter(loaded);
            character = loaded;
        }

        if (character.IsDead)
            return ChangeHandlerResult.Failure($"{id} is already dead.");
        if (!character.IsPc || character.SystemStats is not Dnd5eExtension)
            return ChangeHandlerResult.Failure($"{id} isn't a 5e player character: death saves apply only to those. Record an NPC's fate with $type death.");
        if (character.CurrentHp > 0)
            return ChangeHandlerResult.Failure($"{id} is at {character.CurrentHp} HP; death saves are only rolled at 0 HP.");

        var tally = character.DeathSaves ??= new DeathSaveTally();
        if (tally.Stable)
        {
            ctx.RecordMessage($"{character.Name} is stable at 0 HP; no death save needed.");
            return ChangeHandlerResult.Ok;
        }

        var roll = req.Roll ?? (await _rollService.RollAsync(
            new RollRequest { Tag = "death_save", Expression = "1d20" }, ct)).Result;

        if (roll == 20)
        {
            character.CurrentHp = Math.Min(1, character.MaxHp);
            character.DeathSaves = null;
            ctx.RecordMessage($"{character.Name} death save: natural 20! Back on their feet at 1 HP.");
            return ChangeHandlerResult.Ok;
        }

        if (roll >= 10)
        {
            tally.Successes++;
            if (tally.Successes >= 3)
            {
                tally.Stable = true;
                ctx.RecordMessage($"{character.Name} death save: {roll}, success (3/3). Stable at 0 HP, unconscious.");
            }
            else
            {
                ctx.RecordMessage($"{character.Name} death save: {roll}, success ({tally.Successes}/3, {tally.Failures}/3 failures).");
            }
            return ChangeHandlerResult.Ok;
        }

        tally.Failures += roll == 1 ? 2 : 1;
        ctx.RecordMessage($"{character.Name} death save: {roll}{(roll == 1 ? " (natural 1, two failures)" : "")}, failure ({Math.Min(tally.Failures, 3)}/3).");
        if (tally.Failures < 3)
            return ChangeHandlerResult.Ok;

        return await DeathChangeHandler.DieAsync(
            new DeathChange { CharacterId = character.Id, Cause = "failed death saves" }, character, ctx, ct);
    }
}
