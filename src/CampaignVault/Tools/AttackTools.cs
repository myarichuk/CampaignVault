using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class AttackTools : CampaignToolBase
{
    private readonly MutationTools _mutationTools;

    public AttackTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        MutationTools mutationTools)
        : base(repository, keys)
    {
        _mutationTools = mutationTools ?? throw new ArgumentNullException(nameof(mutationTools));
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Makes an attack (melee, ranged, or attack-mode spell) against one or more targets.
This is the discoverable, structured entry point for combat — prefer this over the generic commit tool for attacks.
Rolls attack + damage via the active ruleset (never invents numbers), enforces whose turn it is and remaining action economy if combat is active, and validates range/reach if spatial positions are tracked.
Requires campaignName. Example: attack(""characters/valen"", [""characters/goblin1""], ""Longsword"", bonus=""2"")")]
    public async Task<ToolResult<CommitResult>> Attack(
        [Description("ID of the attacking character.")]
        string characterId,
        [Description("List of target character IDs.")]
        string[] targetIds,
        [Description("Weapon, spell name, or action label (e.g. 'Longsword', 'Fireball', 'Unarmed Strike').")]
        string actionName,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string? campaignName = null,
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

        if (!string.IsNullOrWhiteSpace(damageDice))
        {
            parameters["damageDice"] = damageDice;
        }

        if (!string.IsNullOrWhiteSpace(bonus))
        {
            parameters["bonus"] = bonus;
        }

        if (!string.IsNullOrWhiteSpace(advantageState))
        {
            parameters["advantageState"] = advantageState;
        }

        if (!string.IsNullOrWhiteSpace(weaponItemId))
        {
            parameters["weaponItemId"] = weaponItemId;
        }

        if (extraParameters != null)
        {
            foreach (var kvp in extraParameters)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }
        }

        return await BuildAndCommitAttackAsync(
            characterId, targetIds, actionName, parameters, narrative, campaignName, isReaction: false);
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Triggers an opportunity attack (reaction) against a target who provoked it.
Opportunity attacks typically occur when a foe disengages, moves away, or provokes by other means.
The reactor must have a reaction available (checked during turn tracking).
Uses the same attack resolution as the Attack tool but consumes the reaction slot instead of an action.
Requires campaignName. Example: trigger_opportunity_attack(""characters/fighter"", ""characters/goblin1"", campaignName=""campaign1"")")]
    public async Task<ToolResult<CommitResult>> TriggerOpportunityAttack(
        [Description("ID of the character making the opportunity attack (the reactor).")]
        string reactorId,
        [Description("ID of the target being attacked.")]
        string targetId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string? campaignName = null,
        [Description("Weapon or action name (e.g. 'Longsword'). If omitted, uses character's held weapon.")]
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

        if (!string.IsNullOrWhiteSpace(damageDice))
        {
            parameters["damageDice"] = damageDice;
        }

        if (!string.IsNullOrWhiteSpace(bonus))
        {
            parameters["bonus"] = bonus;
        }

        var resolvedActionName = actionName ?? "Opportunity Attack";

        return await BuildAndCommitAttackAsync(
            reactorId, [targetId], resolvedActionName, parameters, narrative, campaignName, isReaction: true, reactionTrigger: "opportunity_attack");
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
