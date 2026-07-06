using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class RulesetActionHandler : IWorldChangeHandler
{
    private readonly IRulesetModuleSelector _selector;
    private readonly CampaignDocumentKeys _keys;

    public RulesetActionHandler(
        IRulesetModuleSelector selector,
        CampaignDocumentKeys keys)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public bool ShouldHandle(WorldChange change) => change is RulesetAction;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        if (change is not RulesetAction action)
        {
            return ChangeHandlerResult.Failure("Change is not a RulesetAction.");
        }

        if (string.IsNullOrWhiteSpace(context.CampaignName))
        {
            return new ChangeHandlerResult(false, $"The field {nameof(context.CampaignName)} is required (in the ChangeContext).");
        }

        var effectiveCampaign = context.CampaignName;
        var configId = _keys.Config(effectiveCampaign);
        var config = await context.Session.LoadAsync<CampaignConfig>(configId, ct)
                     ?? new CampaignConfig { Id = configId };

        var module = _selector.GetModule(config.ActiveSystem);

        // Pre-check: action economy gating (turn ownership, action slots)
        if (context.ActiveCombat?.IsActive == true)
        {
            var activeCombat = context.ActiveCombat;
            var combatantState = activeCombat.Combatants.FirstOrDefault(c => c.CharacterId == action.CharacterId);

            if (combatantState != null)
            {
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

                // Reaction slot check (for reactions)
                if (action.IsReaction && !combatantState.ReactionAvailable)
                {
                    return ChangeHandlerResult.Failure($"[NoReactionAvailable] {action.CharacterId} has already reacted this round.");
                }

                if (action.IsReaction)
                {
                    combatantState.ReactionAvailable = false;
                }
            }
        }

        var output = await module.Actions.ResolveAsync(context, action, ct);

        if (!output.Result.Success)
        {
            var msg = string.IsNullOrEmpty(output.Result.ErrorCode) ? output.Result.Narrative : $"[{output.Result.ErrorCode}] {output.Result.Narrative}";
            return ChangeHandlerResult.Failure(msg);
        }

        foreach (var mutation in output.Mutations)
        {
            await context.Dispatcher.DispatchMutationAsync(context, mutation, ct);
        }

        return string.IsNullOrWhiteSpace(output.Result.Narrative)
            ? ChangeHandlerResult.Ok
            : new ChangeHandlerResult(true, output.Result.Narrative);
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

        if (ra.TargetIds != null)
        {
            foreach (var targetId in ra.TargetIds)
            {
                if (!string.IsNullOrEmpty(targetId))
                {
                    characterIds?.Add(targetId);
                    allInvolvedIds?.Add(targetId);
                }
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
