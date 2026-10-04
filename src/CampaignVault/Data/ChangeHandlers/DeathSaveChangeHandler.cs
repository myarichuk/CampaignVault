using CampaignVault.Models;
using CampaignVault.Tools;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// One 5e death saving throw for a player character at 0 HP (<see cref="DeathSaveChange"/>). Three failures kill via
/// <see cref="DeathChangeHandler"/>; three successes stabilise; a natural 20 wakes the character at 1 HP. For a PF2e player
/// character the same change is the recovery check: a flat check against DC 10 + dying that moves the dying value.
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
        if (!character.IsPc || character.SystemStats is not (Dnd5eExtension or Pf2eExtension))
            return ChangeHandlerResult.Failure($"{id} isn't a 5e or PF2e player character: death saves and recovery checks apply only to those. Record an NPC's fate with $type death.");
        if (character.CurrentHp > 0)
            return ChangeHandlerResult.Failure($"{id} is at {character.CurrentHp} HP; death saves and recovery checks are only rolled at 0 HP.");
        if (Pf2eDying.Applies(character) && Pf2eDying.Get(character, Pf2eDying.Dying) <= 0)
            return ChangeHandlerResult.Failure($"{character.Name} isn't dying (unconscious at 0 HP, no dying value): no recovery check is rolled. Healing wakes them.");

        HitPointState.SyncDowned(character);
        var isPf2e = Pf2eDying.Applies(character);
        var tally = isPf2e ? new DeathSaveTally() : character.DeathSaves ??= new DeathSaveTally();
        if (tally.Stable)
        {
            ctx.RecordMessage($"{character.Name} is stable at 0 HP; no death save needed.");
            return ChangeHandlerResult.Ok;
        }

        // On the dying turn the save is the turn's one action: the marker set by turn advancement is spent by it.
        var turnMarker = ctx.ActiveCombat is { IsActive: true } encounter && string.Equals(encounter.ActiveTurnId, id, StringComparison.OrdinalIgnoreCase)
            ? encounter.Combatants.FirstOrDefault(c => string.Equals(c.CharacterId, id, StringComparison.OrdinalIgnoreCase))?.ActionBudget
            : null;
        if (turnMarker is not null && turnMarker.TryGetValue(CombatTools.DeathSaveDueKey, out var due))
        {
            if (due <= 0)
                return ChangeHandlerResult.Failure($"{character.Name} has already rolled the death save for this turn.");
            turnMarker[CombatTools.DeathSaveDueKey] = 0;
        }

        if (isPf2e)
            return await RecoveryCheckAsync(req, character, ctx, ct);

        // The save goes through the roll pipeline so a Bless-style bonus counts; the natural d20 decides 1 and 20, the total decides 10.
        var resolution = ctx.ResolveRollModifiers(
            new RollQuery(RollKinds.DeathSave, null, [], character, null, DamageApplication.SystemOf(character),
                await ctx.GetSystemOptionsAsync()), 0);
        var die = req.Roll ?? (await _rollService.RollAsync(
            new RollRequest
            {
                Tag = "death_save",
                Expression = "1d20",
                Mechanic = resolution.Advantage switch
                {
                    AdvantageEffect.Advantage => DiceMechanic.Advantage,
                    AdvantageEffect.Disadvantage => DiceMechanic.Disadvantage,
                    _ => DiceMechanic.Standard,
                },
            }, ct)).Result;
        var roll = die + resolution.Bonus;
        var bonusNote = resolution.Bonus != 0 ? $" (d20 {die}{resolution.Bonus:+#;-#})" : "";

        if (die == 20)
        {
            HitPointState.Set(character, Math.Min(1, character.MaxHp));
            ctx.RecordMessage($"{character.Name} death save: natural 20! Back on their feet at 1 HP.");
            return ChangeHandlerResult.Ok;
        }

        if (roll >= 10)
        {
            tally.Successes++;
            if (tally.Successes >= 3)
            {
                tally.Stable = true;
                ctx.RecordMessage($"{character.Name} death save: {roll}{bonusNote}, success (3/3). Stable at 0 HP, unconscious.");
            }
            else
            {
                ctx.RecordMessage($"{character.Name} death save: {roll}{bonusNote}, success ({tally.Successes}/3, {tally.Failures}/3 failures).");
            }
            return ChangeHandlerResult.Ok;
        }

        tally.Failures += die == 1 ? 2 : 1;
        ctx.RecordMessage($"{character.Name} death save: {roll}{bonusNote}{(die == 1 ? " (natural 1, two failures)" : "")}, failure ({Math.Min(tally.Failures, 3)}/3).");
        if (tally.Failures < 3)
            return ChangeHandlerResult.Ok;

        return await DeathChangeHandler.DieAsync(
            new DeathChange { CharacterId = character.Id, Cause = "failed death saves" }, character, ctx, ct);
    }

    /// <summary>
    /// PF2e recovery check: a flat d20 against DC 10 + dying, with no modifiers. Critical success lowers dying by 2, success by
    /// 1, failure raises it by 1, critical failure by 2 (a natural 20 or 1 moves the result one step). Dying 4, less doomed,
    /// is death. Dying that falls to 0 ends and adds one to wounded; the character stays unconscious at 0 HP until healed.
    /// </summary>
    private async Task<ChangeHandlerResult> RecoveryCheckAsync(DeathSaveChange req, Character character, ChangeContext ctx, CancellationToken ct)
    {
        var dying = Pf2eDying.Get(character, Pf2eDying.Dying);
        var die = req.Roll ?? (await _rollService.RollAsync(
            new RollRequest { Tag = "recovery_check", Expression = "1d20", Mechanic = DiceMechanic.Standard }, ct)).Result;
        var dc = 10 + dying;
        var degree = Pf2eDying.Degree(die, dying);
        var label = degree switch
        {
            RecoveryDegree.CriticalSuccess => "critical success",
            RecoveryDegree.Success => "success",
            RecoveryDegree.Failure => "failure",
            _ => "critical failure",
        };
        var head = $"{character.Name} recovery check: {die} vs DC {dc}, {label}.";

        var after = dying + Pf2eDying.DyingChange(degree);
        if (Pf2eDying.IsDeadlyValue(character, after))
        {
            ctx.RecordMessage($"{head} Dying {after}: that is {Pf2eDying.DeathThreshold(character)} or more.");
            return await DeathChangeHandler.DieAsync(
                new DeathChange { CharacterId = character.Id, Cause = "dying" }, character, ctx, ct);
        }

        if (after <= 0)
        {
            Pf2eDying.Set(character, Pf2eDying.Dying, 0);
            var wounded = Pf2eDying.Get(character, Pf2eDying.Wounded) + 1;
            Pf2eDying.Set(character, Pf2eDying.Wounded, wounded);
            ctx.RecordMessage($"{head} Dying ends: unconscious at 0 HP and wounded {wounded}. Any healing wakes them.");
            return ChangeHandlerResult.Ok;
        }

        Pf2eDying.Set(character, Pf2eDying.Dying, after);
        ctx.RecordMessage($"{head} Dying {dying} → {after}.");
        return ChangeHandlerResult.Ok;
    }
}
