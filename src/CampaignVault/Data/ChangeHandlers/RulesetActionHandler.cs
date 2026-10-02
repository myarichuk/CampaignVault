using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RulesetActionHandler(
    IRulesetModuleSelector selector,
    CampaignDocumentKeys keys,
    SpellDefinitionProvider spellProvider,
    FeatDefinitionProvider featProvider,
    ProgressionDefinitionProvider? progressionProvider = null,
    RaceDefinitionProvider? raceProvider = null)
    : IWorldChangeHandler
{
    private readonly IRulesetModuleSelector _selector = selector ?? throw new ArgumentNullException(nameof(selector));
    private readonly CampaignDocumentKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    private readonly SpellDefinitionProvider _spellProvider = spellProvider ?? throw new ArgumentNullException(nameof(spellProvider));
    private readonly FeatDefinitionProvider _featProvider = featProvider ?? throw new ArgumentNullException(nameof(featProvider));

    public bool ShouldHandle(WorldChange change) => change is RulesetAction;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        if (change is not RulesetAction surgeAction
            || !surgeAction.Parameters.TryGetValue("actionSurge", out var surgeRaw)
            || !bool.TryParse(surgeRaw, out var surge) || !surge)
        {
            return await ApplyActionAsync(change, context, ct);
        }

        // Action Surge: spend the pool and add one action for this turn, then run the action normally.
        // If that action then fails, the surge is refunded so a rejected attack never burns the class feature.
        var (failure, undo) = await BeginActionSurgeAsync((ChangeContext)context, surgeAction, ct);
        if (failure is { } refused)
        {
            return refused;
        }

        var result = await ApplyActionAsync(change, context, ct);
        if (!result.Success)
        {
            undo!();
            return result;
        }

        return new ChangeHandlerResult(true, $"Action Surge spent (+1 action this turn). {result.Message}");
    }

    private static async Task<(ChangeHandlerResult? Failure, Action? Undo)> BeginActionSurgeAsync(
        ChangeContext ctx, RulesetAction action, CancellationToken ct)
    {
        if (ctx.ActiveCombat?.IsActive != true)
        {
            return (ChangeHandlerResult.Failure("[ActionSurge] Action Surge only applies during active combat."), null);
        }

        var state = ctx.ActiveCombat.Combatants.FirstOrDefault(c => c.CharacterId == action.CharacterId);
        if (state is null)
        {
            return (ChangeHandlerResult.Failure($"[NotInCombat] {action.CharacterId} is not on the active combat roster."), null);
        }

        if (!ctx.Characters.TryGetValue(action.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(action.CharacterId, ct);
            if (character is null)
            {
                return (ChangeHandlerResult.Failure($"Character '{action.CharacterId}' not found."), null);
            }

            ctx.RegisterNewCharacter(character);
        }

        var pools = character.SystemStats?.ResourcePools;
        if (pools is null || !pools.TryGetValue("action_surge", out var pool) || pool.Current < 1)
        {
            return (ChangeHandlerResult.Failure(
                $"[ActionSurge] {character.Name} has no Action Surge use left (pool 'action_surge'). "
                + "If the pool is missing, re-commit systemStats to re-derive class pools."), null);
        }

        if (state.ActionBudget.ContainsKey("actionSurgeUsed"))
        {
            return (ChangeHandlerResult.Failure("[ActionSurge] Action Surge was already used this turn."), null);
        }

        pools["action_surge"] = pool with { Current = pool.Current - 1 };
        state.ActionBudget["action"] = state.ActionBudget.GetValueOrDefault("action") + 1;
        state.ActionBudget["actionSurgeUsed"] = 1;

        return (null, () =>
        {
            pools["action_surge"] = pool;
            state.ActionBudget["action"] = Math.Max(0, state.ActionBudget.GetValueOrDefault("action") - 1);
            state.ActionBudget.Remove("actionSurgeUsed");
        });
    }

    private async Task<ChangeHandlerResult> ApplyActionAsync(
        WorldChange change, IChangeContext context, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        if (change is not RulesetAction action)
        {
            return ChangeHandlerResult.Failure("Change is not a RulesetAction.");
        }

        if (string.IsNullOrWhiteSpace(ctx.CampaignName))
        {
            return new ChangeHandlerResult(false, $"The field {nameof(ctx.CampaignName)} is required (in the ChangeContext).");
        }

        var effectiveCampaign = ctx.CampaignName;
        var configId = _keys.Config(effectiveCampaign);
        var config = await ctx.Session.LoadAsync<CampaignConfig>(configId, ct)
                     ?? new CampaignConfig { Id = configId };

        var module = _selector.GetModule(config.ActiveSystem);

        // Pre-check: action economy gating (turn ownership, action slots)
        if (ctx.ActiveCombat?.IsActive == true)
        {
            var activeCombat = ctx.ActiveCombat;
            var combatantState = activeCombat.Combatants.FirstOrDefault(c => c.CharacterId == action.CharacterId);
            if (combatantState == null)
            {
                return ChangeHandlerResult.Failure(
                    $"[NotInCombat] {action.CharacterId} is not on the active combat roster.");
            }

            // Turn ownership check (unless this is a reaction). A bound minion acts
            // on its controller's turn via the same ordinary ruleset_action — no new
            // action type. The exception is deliberately narrow: the actor must be
            // live-linked (ControlledById set, binding present and not lapsed) to
            // the exact character whose turn is active. Anything else — unknown
            // actor, no link, lapsed binding, another master's turn — keeps the
            // previous failure. Minions still consume their own action slots below.
            if (!action.IsReaction && activeCombat.ActiveTurnId != action.CharacterId
                && !ActsOnControllersTurn(context, action.CharacterId, activeCombat.ActiveTurnId))
            {
                return ChangeHandlerResult.Failure($"[NotYourTurn] {action.CharacterId} cannot act — it is {activeCombat.ActiveTurnId}'s turn.");
            }

            // Action slot consumption check
            if (!module.Combat.TryConsumeActionSlot(combatantState, action, out var slotError))
            {
                return ChangeHandlerResult.Failure($"[NoActionAvailable] {slotError}");
            }

            switch (action.IsReaction)
            {
                // Reaction slot check (for reactions)
                case true when !combatantState.ReactionAvailable:
                    return ChangeHandlerResult.Failure($"[NoReactionAvailable] {action.CharacterId} has already reacted this round.");
                case true:
                    combatantState.ReactionAvailable = false;
                    break;
            }
        }

        // Feat effects live for everyone this action touches (SRD or homebrew, plugin/mode gated); the resolver folds them into rolls.
        var involved = new[] { action.CharacterId }.Concat(action.TargetIds)
            .Where(id => ctx.Characters.ContainsKey(id)).Distinct()
            .Select(id => ctx.Characters[id]);
        action.FeatEffects = await FeatEffectRules.ResolveAsync(
            ctx.Session, _featProvider, config.ActiveSystem, involved, effectiveCampaign, [.. ctx.ActiveModes.Keys], progressionProvider, raceProvider);

        // Merge weapon-derived defaults (including "range") before range validation runs,
        // so weapon-based range enforcement (the documented, primary path) actually has data to check.
        AmmoResolver.AmmoPlan? ammoPlan = null;
        if (action.ActionType == RulesetActionType.Attack)
        {
            var callerSetCount = AttackTargetHelper.HasExplicitCount(action);
            await WeaponParameterResolver.ApplyHeldWeaponDefaultsAsync(action, ctx, ct);

            // Ranged weapons that declare an ammoType fire real rounds: fire mode, ammo lookup, clamp to what is left.
            var (ammoFailure, plan) = await AmmoResolver.PrepareAsync(action, ctx, callerSetCount, ct);
            if (ammoFailure is { } failed)
            {
                return failed;
            }

            ammoPlan = plan;
        }

        // Pre-check: range/AoE validation (only if the ruleset enforces it)
        if (module.Combat.EnforcesRange)
        {
            if (!RangeValidationHelper.Validate(action, ctx, out var rangeError))
            {
                return ChangeHandlerResult.Failure($"[OutOfRange] {rangeError}");
            }
        }

        // Pre-check: spell component gating (Verbal/Somatic/Material vs. caster's condition state).
        if (action.ActionType == RulesetActionType.Spell)
        {
            var componentFailure = await EvaluateSpellComponentsAsync(action, ctx, ct);
            if (componentFailure != null)
            {
                return componentFailure.Value;
            }
        }

        var output = await module.Actions.ResolveAsync(ctx, action, ct);

        if (!output.Result.Success)
        {
            var msg = string.IsNullOrEmpty(output.Result.ErrorCode) ? output.Result.Narrative : $"[{output.Result.ErrorCode}] {output.Result.Narrative}";
            return ChangeHandlerResult.Failure(msg);
        }

        ctx.AutoApplyDepth++;
        try
        {
            foreach (var mutation in output.Mutations)
            {
                await ctx.Dispatcher.DispatchMutationAsync(ctx, mutation, ct);
            }
        }
        finally
        {
            ctx.AutoApplyDepth--;
        }

        var narrative = output.Result.Narrative;
        if (ammoPlan is not null)
        {
            var report = AmmoResolver.Spend(ammoPlan);
            narrative = string.IsNullOrWhiteSpace(narrative) ? report : narrative + " " + report;
        }

        foreach (var line in await ResolveSecretsAsync(action, output.Result, ctx, ct))
        {
            narrative = string.IsNullOrWhiteSpace(narrative) ? line : narrative + " " + line;
        }

        return string.IsNullOrWhiteSpace(narrative)
            ? ChangeHandlerResult.Ok
            : new ChangeHandlerResult(true, narrative);
    }

    /// <summary>
    /// Minion-turn rule: a live-bound minion may act when its controller's turn is
    /// active. Pure in-memory id comparison — no session access, no new failure
    /// modes: any absent data returns false and the caller keeps the old behavior.
    /// </summary>
    internal static bool ActsOnControllersTurn(IChangeContext context, string actorId, string? activeTurnId)
    {
        if (string.IsNullOrEmpty(activeTurnId)
            || !context.Characters.TryGetValue(actorId, out var actor)
            || string.IsNullOrEmpty(actor.ControlledById)
            || actor.MinionBinding is not { ControlLapsed: false })
        {
            return false;
        }

        return string.Equals(actor.ControlledById, activeTurnId, StringComparison.Ordinal);
    }

    /// <summary>T5c: a skill check at a location resolves against its secrets, as dice do. A check whose
    /// parameters carry "disarm" (a hazard's name) tries to make that hazard safe; an Investigation/
    /// Perception check reveals every hidden exit, item, detail and trap its total meets.</summary>
    private static async Task<List<string>> ResolveSecretsAsync(RulesetAction action, ResolverResult result, ChangeContext ctx, CancellationToken ct)
    {
        if (action.ActionType != RulesetActionType.SkillCheck || result.RollTotal is not { } total
            || !ctx.Characters.TryGetValue(action.CharacterId, out var actor)
            || string.IsNullOrEmpty(actor.CurrentLocationId))
        {
            return [];
        }

        if (action.Parameters.TryGetValue("disarm", out var hazardName) && !string.IsNullOrWhiteSpace(hazardName))
        {
            var disarm = await HiddenContent.DisarmAsync(ctx.Session, actor.CurrentLocationId, hazardName, total, actor.Name, ct);
            return disarm == null ? [] : [disarm];
        }

        return HiddenContent.IsSearchSkill(result.Skill)
            ? await HiddenContent.DiscoverAsync(ctx.Session, actor.CurrentLocationId, total, "FOUND", actor.Name, ct)
            : [];
    }

    /// <summary>
    /// Gates a Spell action on the caster's condition state. Returns a hard-failure result to
    /// block the cast, or null to let resolution proceed (possibly after recording a soft warning).
    /// See CastingComponentGate for the StatModifiers-tag convention this relies on.
    /// </summary>
    private async Task<ChangeHandlerResult?> EvaluateSpellComponentsAsync(
        RulesetAction action, IChangeContext context, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        if (!context.Characters.TryGetValue(action.CharacterId, out var character) || character.SystemStats == null)
        {
            return null;
        }

        var statusEffects = character.SystemStats.StatusEffects;

        // Hard block: standard incapacitation prevents any action, independent of spell data.
        if (ActionBlock.IsBlocked(character, out var blocker))
        {
            return ChangeHandlerResult.Failure(
                $"[SpellcastingBlocked] {character.Name} is {blocker!.Name} and cannot cast spells.");
        }

        if (!RulesetSystemResolver.TryFromStats(character.SystemStats, out var system) || string.IsNullOrWhiteSpace(action.ActionName))
        {
            return null;
        }

        var components = await CastingComponentGate.ResolveSpellComponentsAsync(
            ctx.Session, _spellProvider, system!, action.ActionName, context.CampaignName);

        if (components == null)
        {
            // Unknown spell name (neither SRD nor homebrew) — nothing to check against.
            return null;
        }

        bool HasTag(string key) => statusEffects.Any(e => e.StatModifiers.TryGetValue(key, out var v) && v != 0);

        if (components.Verbal && (HasTag(CastingComponentGate.BlocksAllActions) || HasTag(CastingComponentGate.BlocksVerbal))
            && !HasTag(CastingComponentGate.WaivesVerbal))
        {
            return ChangeHandlerResult.Failure(
                $"[SpellcastingBlocked] {character.Name} cannot supply the Verbal component for {action.ActionName}.");
        }

        if (components.Somatic && (HasTag(CastingComponentGate.BlocksAllActions) || HasTag(CastingComponentGate.BlocksSomatic))
            && !HasTag(CastingComponentGate.WaivesSomatic))
        {
            var knownFeats = CastingComponentGate.GetKnownFeatNames(character.SystemStats);
            var hasPassiveWaiver = await CastingComponentGate.HasCastingWaiverAsync(
                ctx.Session, _featProvider, system!, knownFeats,
                CastingComponentGate.SomaticHandsFullWaiver, context.CampaignName);

            if (!hasPassiveWaiver)
            {
                return ChangeHandlerResult.Failure(
                    $"[SpellcastingBlocked] {character.Name} cannot supply the Somatic component for {action.ActionName}.");
            }
        }

        if (components.Material && (HasTag(CastingComponentGate.BlocksAllActions) || HasTag(CastingComponentGate.BlocksMaterial))
            && !HasTag(CastingComponentGate.WaivesMaterial))
        {
            return ChangeHandlerResult.Failure(
                $"[SpellcastingBlocked] {character.Name} cannot supply the Material component for {action.ActionName}.");
        }

        // Soft fallback: an untagged homebrew/narrative condition whose NAME reads like it
        // could restrain casting (bound hands, a gag, "cannot speak") is active alongside a
        // component-requiring spell. The engine can't judge it, so it flags rather than
        // ignores. F2: purely narrative buffs (Mage Armor, Bless) no longer warn on every
        // cast — only names that plausibly interfere with Verbal/Somatic components do.
        if (components.Verbal || components.Somatic || components.Material)
        {
            var untaggedHomebrew = statusEffects.FirstOrDefault(e =>
                e.ConditionName == null
                && LooksLikeComponentBlocker(e.Name)
                && !e.StatModifiers.Keys.Any(k => k is CastingComponentGate.BlocksVerbal or CastingComponentGate.BlocksSomatic
                    or CastingComponentGate.BlocksMaterial or CastingComponentGate.BlocksAllActions
                    or CastingComponentGate.WaivesVerbal or CastingComponentGate.WaivesSomatic or CastingComponentGate.WaivesMaterial));

            if (untaggedHomebrew != null)
            {
                context.RecordMessage(
                    $"[WARNING] {character.Name} has narrative condition '{untaggedHomebrew.Name}' active — verify this doesn't prevent " +
                    $"Verbal/Somatic/Material components before resolving {action.ActionName}. If it does, tag it with BlocksVerbalComponents/" +
                    "BlocksSomaticComponents/BlocksMaterialComponents so the engine enforces it going forward.");
            }
        }

        return null;
    }

    /// <summary>F2: does this untagged narrative condition's name read like it could
    /// interfere with spell components (gagged mouth, bound hands, "cannot speak")?
    /// Purely narrative buffs (Mage Armor, Bless, Brave) return false, so casting with
    /// them active stays quiet.</summary>
    internal static bool LooksLikeComponentBlocker(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return ComponentBlockerNameFragments.Any(
            fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] ComponentBlockerNameFragments =
    [
        "silenc", "gag", "mute",
        "bound", "bind", "tied", "restrain", "manacl", "shackl",
        "paralyz", "petrifi", "stun", "unconscious", "incapacitat",
        "cannot", "can't", "unable", "prevent", "block",
    ];

    public bool ExtractInvolvedEntities(
        WorldChange change,
        HashSet<string>? characterIds = null,
        HashSet<string>? locationIds = null,
        HashSet<string>? factionIds = null,
        HashSet<string>? questIds = null,
        HashSet<string>? itemIds = null,
        HashSet<string>? allInvolvedIds = null)
    {
        if (change is not RulesetAction ra) return false;

        if (!string.IsNullOrEmpty(ra.CharacterId))
        {
            characterIds?.Add(ra.CharacterId);
            allInvolvedIds?.Add(ra.CharacterId);
        }

        foreach (var targetId in ra.TargetIds)
        {
            if (!string.IsNullOrEmpty(targetId))
            {
                characterIds?.Add(targetId);
                allInvolvedIds?.Add(targetId);
            }
        }

        if (WeaponParameterResolver.TryExtractWeaponItemId(ra.Parameters, out var weaponItemId))
        {
            itemIds?.Add(weaponItemId);
            allInvolvedIds?.Add(weaponItemId);
        }

        return true;
    }
}
