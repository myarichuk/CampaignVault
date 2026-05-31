using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class Fallout2d20RulesetResolver : IRulesetResolver
{
    private readonly IRollService _rollService;

    public Fallout2d20RulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
    }

    public RulesetSystem System => RulesetSystem.Fallout2d20;

    public async Task<ResolverOutput> ResolveAsync(ChangeContext context, RulesetAction action, CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.ActorId, out var actor))
            return new ResolverOutput { Result = new ResolverResult { Narrative = $"Error: Actor '{action.ActorId}' not found." } };

        var actorStats = actor.SystemStats as Fallout2d20Extension ?? new Fallout2d20Extension();
        var mutations = new List<WorldChange>();
        string narrative;

        switch (action.ActionType)
        {
            case RulesetActionType.Attack:
                narrative = await ResolveAttackAsync(action, context, actorStats, mutations, ct);
                break;
            case RulesetActionType.SkillCheck:
                narrative = await ResolveSkillCheckAsync(action, actorStats, ct);
                break;
            default:
                narrative = $"Fallout 2d20: Action type {action.ActionType} not yet fully implemented.";
                break;
        }

        return new ResolverOutput { Mutations = mutations, Result = new ResolverResult { Narrative = narrative } };
    }

    private int GetAttributeValue(Fallout2d20Extension stats, string name)
    {
        return name.ToLower() switch
        {
            "strength" => stats.Strength,
            "perception" => stats.Perception,
            "endurance" => stats.Endurance,
            "charisma" => stats.Charisma,
            "intelligence" => stats.Intelligence,
            "agility" => stats.Agility,
            "luck" => stats.Luck,
            _ => 5
        };
    }

    private async Task<string> ResolveSkillCheckAsync(RulesetAction action, Fallout2d20Extension actorStats, CancellationToken ct)
    {
        int difficulty = action.Parameters.TryGetValue("difficulty", out var diffStr) ? int.Parse(diffStr) : 1;
        
        string attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        string skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        int attrVal = GetAttributeValue(actorStats, attribute);
        int skillVal = actorStats.Skills.TryGetValue(skill, out var s) ? s : 0;
        int targetNumber = attrVal + skillVal;
        
        bool isTagged = actorStats.TagSkills.Contains(skill);
        int? critThreshold = isTagged ? skillVal : null;
        
        int poolSize = action.Parameters.TryGetValue("pool", out var p) ? int.Parse(p) : 2;

        var request = new RollRequest
        {
            Tag = "skill",
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
            CriticalThreshold = critThreshold
        };

        var outcome = await _rollService.RollAsync(request, ct);
        
        bool success = outcome.Successes >= difficulty;
        int apGenerated = Math.Max(0, outcome.Successes - difficulty);
        string compMsg = outcome.HasComplication ? " COMPLICATION ROLLED!" : "";
        
        return $"{action.ActionName} ({attribute}+{skill} TN {targetNumber}): {(success ? "Success" : "Failure")}. Generated {apGenerated} AP.{compMsg} {outcome.Summary}";
    }

    private async Task<string> ResolveAttackAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
            return "Error: No valid target specified for attack.";

        var targetStats = target.SystemStats as Fallout2d20Extension ?? new Fallout2d20Extension();
        
        int difficulty = action.Parameters.TryGetValue("difficulty", out var diffStr) ? int.Parse(diffStr) : targetStats.Defense;
        
        string attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        string skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        int attrVal = GetAttributeValue(actorStats, attribute);
        int skillVal = actorStats.Skills.TryGetValue(skill, out var s) ? s : 0;
        int targetNumber = attrVal + skillVal;
        bool isTagged = actorStats.TagSkills.Contains(skill);
        
        int poolSize = action.Parameters.TryGetValue("pool", out var p) ? int.Parse(p) : 2;

        var request = new RollRequest
        {
            Tag = "attack",
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
            CriticalThreshold = isTagged ? skillVal : null
        };

        var outcome = await _rollService.RollAsync(request, ct);
        bool success = outcome.Successes >= difficulty;
        string compMsg = outcome.HasComplication ? " COMPLICATION!" : "";

        if (!success)
            return $"{action.ActionName}: Missed.{compMsg} {outcome.Summary}";

        int combatDiceCount = action.Parameters.TryGetValue("damageDice", out var cd) ? int.Parse(cd) : 3;
        var combatResult = await _rollService.RollFalloutCombatDiceAsync(combatDiceCount, ct);

        int dr = 0; // In a full implementation, read damageResistance from targetStats
        int finalDamage = Math.Max(0, combatResult.Damage - dr);

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        return $"{action.ActionName}: Hit for {finalDamage} damage ({combatResult.Effects} Effects).{compMsg}";
    }

    public async Task<float> RollInitiativeAsync(IAsyncDocumentSession session, string characterId, CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null) return 0f;
        var stats = character.SystemStats as Fallout2d20Extension ?? new Fallout2d20Extension();
        return stats.Perception + stats.Agility;
    }
}
