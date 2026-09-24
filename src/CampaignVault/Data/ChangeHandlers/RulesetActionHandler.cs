using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RulesetActionHandler(
    IRulesetModuleSelector selector,
    CampaignDocumentKeys keys,
    SpellDefinitionProvider spellProvider,
    FeatDefinitionProvider featProvider)
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

            // Turn ownership check (unless this is a reaction)
            if (!action.IsReaction && activeCombat.ActiveTurnId != action.CharacterId)
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

        // Merge weapon-derived defaults (including "range") before range validation runs,
        // so weapon-based range enforcement (the documented, primary path) actually has data to check.
        if (action.ActionType == RulesetActionType.Attack)
        {
            await WeaponParameterResolver.ApplyHeldWeaponDefaultsAsync(action, ctx, ct);
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
        foreach (var line in await ResolveSecretsAsync(action, output.Result, ctx, ct))
        {
            narrative = string.IsNullOrWhiteSpace(narrative) ? line : narrative + " " + line;
        }

        return string.IsNullOrWhiteSpace(narrative)
            ? ChangeHandlerResult.Ok
            : new ChangeHandlerResult(true, narrative);
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
        var blocker = statusEffects.FirstOrDefault(e => CastingComponentGate.HardBlockConditions.Contains(e.Name));
        if (blocker != null)
        {
            return ChangeHandlerResult.Failure(
                $"[SpellcastingBlocked] {character.Name} is {blocker.Name} and cannot cast spells.");
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

        // Soft fallback: an untagged homebrew/narrative condition is active alongside a
        // component-requiring spell. The engine can't judge it, so it flags rather than ignores.
        if (components.Verbal || components.Somatic || components.Material)
        {
            var untaggedHomebrew = statusEffects.FirstOrDefault(e =>
                e.ConditionName == null
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
