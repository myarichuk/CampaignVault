using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class AttackTools : CampaignToolBase, IMcpServerTool
{
    private readonly MutationTools _mutationTools;

    public AttackTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        MutationTools mutationTools,
        ILogger<AttackTools>? logger = null)
        : base(repository, keys, logger)
    {
        _mutationTools = mutationTools ?? throw new ArgumentNullException(nameof(mutationTools));
    }

    internal async Task<ToolResult<CommitResult>> Attack(
        [Description("ID of the attacking character.")]
        string characterId,
        [Description("List of target character IDs.")]
        string[] targetIds,
        [Description("Weapon or melee action label (e.g. 'Longsword', 'Unarmed Strike'). For spells/ranged attacks, use ruleset_action instead.")]
        string actionName,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Damage dice expression (e.g. '1d8', '2d6+1'). Optional; weapon defaults apply if omitted.")]
        string? damageDice = null,
        [Description("Attack roll bonus, separate from weapon defaults (e.g. '2' for a +2 modifier).")]
        string? bonus = null,
        [Description("5e only: 'Advantage', 'Disadvantage', or 'None'.")]
        string? advantageState = null,
        [Description("ID of the weapon item to use (e.g. 'items/longsword_1'). If omitted, uses character's held weapons.")]
        string? weaponItemId = null,
        [Description("Extra parameters for ruleset-specific behavior (e.g. {'actionCost': '2'} for PF2e, {'bonusAction': 'true'} for 5e bonus action attacks).")]
        Dictionary<string, string>? extraParameters = null,
        [Description("Narrative description of the attack (e.g. 'Valen lunges forward'). If omitted, a default message is generated.")]
        string? narrative = null)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "characterId is required.");
        }

        if (targetIds == null || targetIds.Length == 0)
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "targetIds must be a non-empty list of target character IDs.");
        }

        if (string.IsNullOrWhiteSpace(actionName))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "actionName is required (e.g. 'Longsword', 'Fireball').");
        }

        var parameters = new Dictionary<string, string>();
        AddIfPresent(parameters, "damageDice", damageDice);
        AddIfPresent(parameters, "bonus", bonus);
        AddIfPresent(parameters, "advantageState", advantageState);
        AddIfPresent(parameters, "weaponItemId", weaponItemId);

        if (extraParameters != null)
        {
            foreach (var kvp in extraParameters)
            {
                AddIfPresent(parameters, kvp.Key, kvp.Value);
            }
        }

        return await BuildAndCommitAttackAsync(
            characterId, targetIds, actionName, parameters, narrative, campaignName, isReaction: false);
    }

    internal async Task<ToolResult<CommitResult>> TriggerOpportunityAttack(
        [Description("ID of the character making the opportunity attack (the reactor).")]
        string reactorId,
        [Description("ID of the target being attacked.")]
        string targetId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Weapon or action name (e.g. 'Longsword'). If omitted, resolves to the character's held weapon ONLY when they carry exactly one weapon — with multiple weapons held, pass a name or weaponItemId or the attack resolves unarmed/default damage.")]
        string? actionName = null,
        [Description("Damage dice expression (e.g. '1d8+2'). Optional; weapon defaults apply if omitted.")]
        string? damageDice = null,
        [Description("Attack roll bonus (e.g. '2'). Optional.")]
        string? bonus = null,
        [Description("Narrative description of the opportunity attack (e.g. 'Fighter swings as goblin flees'). If omitted, a default message is generated.")]
        string? narrative = null)
    {
        if (string.IsNullOrWhiteSpace(reactorId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "reactorId is required.");
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "targetId is required.");
        }

        var parameters = new Dictionary<string, string>();
        AddIfPresent(parameters, "damageDice", damageDice);
        AddIfPresent(parameters, "bonus", bonus);

        var resolvedActionName = actionName ?? "Opportunity Attack";

        return await BuildAndCommitAttackAsync(
            reactorId, [targetId], resolvedActionName, parameters, narrative, campaignName, isReaction: true, reactionTrigger: "opportunity_attack");
    }

    private static void AddIfPresent(Dictionary<string, string> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[key] = value;
        }
    }

    private async Task<ToolResult<CommitResult>> BuildAndCommitAttackAsync(
        string characterId,
        string[] targetIds,
        string actionName,
        Dictionary<string, string> parameters,
        string? narrative,
        string? campaignName,
        bool isReaction = false,
        string? reactionTrigger = null)
    {
        var action = new RulesetAction
        {
            CharacterId = characterId,
            TargetIds = targetIds.ToList(),
            ActionName = actionName,
            ActionType = RulesetActionType.Attack,
            ActionCategory = ActionCategory.Melee,
            Parameters = parameters,
            IsReaction = isReaction,
            ReactionTrigger = reactionTrigger
        };

        var narrativeText = narrative ?? (isReaction
            ? $"{characterId} makes an opportunity attack against {string.Join(", ", targetIds)} with {actionName}."
            : $"{characterId} attacks {string.Join(", ", targetIds)} with {actionName}.");

        return await _mutationTools.Commit([action], narrativeText, campaignName);
    }
}
