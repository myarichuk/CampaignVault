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

        if (actor.SystemStats is not Fallout2d20Extension actorStats)
            return new ResolverOutput { Result = new ResolverResult { Narrative = $"Error: Character uses incompatible ruleset stats for current ActiveSystem." } };
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
        int difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
            return $"Error: invalid difficulty value '{diffStr}'.";
        
        string attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        string skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        int attrVal = GetAttributeValue(actorStats, attribute);
        int skillVal = actorStats.Skills.TryGetValue(skill, out var s) ? s : 0;
        int targetNumber = attrVal + skillVal;
        
        bool isTagged = actorStats.TagSkills.Contains(skill);
        int? critThreshold = isTagged ? skillVal : null;
        
        int poolSize = 2;
        if (action.Parameters.TryGetValue("pool", out var p) && !int.TryParse(p, out poolSize))
            return $"Error: invalid pool value '{p}'.";

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

        if (target.SystemStats is not Fallout2d20Extension targetStats)
            return "Error: Target uses incompatible ruleset stats for current ActiveSystem.";
        
        int difficulty = targetStats.Defense;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
            return $"Error: invalid difficulty value '{diffStr}'.";
        
        string attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        string skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        int attrVal = GetAttributeValue(actorStats, attribute);
        int skillVal = actorStats.Skills.TryGetValue(skill, out var s) ? s : 0;
        int targetNumber = attrVal + skillVal;
        bool isTagged = actorStats.TagSkills.Contains(skill);
        
        int poolSize = 2;
        if (action.Parameters.TryGetValue("pool", out var p) && !int.TryParse(p, out poolSize))
            return $"Error: invalid pool value '{p}'.";

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

        int combatDiceCount = 3;
        if (action.Parameters.TryGetValue("damageDice", out var cd) && !int.TryParse(cd, out combatDiceCount))
            return $"Error: invalid damageDice value '{cd}'.";
        string damageType = action.Parameters.TryGetValue("damageType", out var dt) ? dt : "Physical";

        var combatResult = await _rollService.RollFalloutCombatDiceAsync(combatDiceCount, ct);

        int dr = targetStats.DamageResistance.TryGetValue(damageType, out var res) ? res : 0;
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
