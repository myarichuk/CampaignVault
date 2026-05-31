using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class Fallout2d20RulesetResolver : RulesetResolverBase<Fallout2d20Extension>
{
    private readonly IRollService _rollService;

    public Fallout2d20RulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
    }

    public override RulesetSystem System => RulesetSystem.Fallout2d20;


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

    protected override async Task<string> ResolveSkillCheckAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        int difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
            return $"Error: invalid difficulty value '{diffStr}'.";
        
        // Lower difficulty is better for the actor. Positive "SkillCheck" modifiers are good.
        // Fallout is TN based, so modifiers add to the TN, but if there's a difficulty modifier we could apply it here.
        // Let's modify the TN instead.
        
        string attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        string skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        int attrVal = GetAttributeValue(actorStats, attribute);
        int skillVal = actorStats.Skills.TryGetValue(skill, out var s) ? s : 0;
        int targetNumber = attrVal + skillVal;
        
        targetNumber = actorStats.ApplyModifiers("SkillCheck", targetNumber);
        targetNumber = actorStats.ApplyModifiers(skill, targetNumber);
        targetNumber = actorStats.ApplyModifiers(attribute, targetNumber);
        
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

    protected override async Task<string> ResolveAttackAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
            return "Error: No valid target specified for attack.";

        if (target.SystemStats is not Fallout2d20Extension targetStats)
            return "Error: Target uses incompatible ruleset stats for current ActiveSystem.";
        
        int defense = targetStats.Defense;
        defense = targetStats.ApplyModifiers("Defense", defense);
        
        int difficulty = defense;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
            return $"Error: invalid difficulty value '{diffStr}'.";
        
        string attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        string skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        int attrVal = GetAttributeValue(actorStats, attribute);
        int skillVal = actorStats.Skills.TryGetValue(skill, out var s) ? s : 0;
        int targetNumber = attrVal + skillVal;
        
        targetNumber = actorStats.ApplyModifiers("AttackRoll", targetNumber);
        targetNumber = actorStats.ApplyModifiers(skill, targetNumber);
        targetNumber = actorStats.ApplyModifiers(attribute, targetNumber);
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
            
        combatDiceCount = actorStats.ApplyModifiers("DamageRoll", combatDiceCount);
        string damageType = action.Parameters.TryGetValue("damageType", out var dt) ? dt : "Physical";

        var combatResult = await _rollService.RollFalloutCombatDiceAsync(combatDiceCount, ct);

        int dr = targetStats.DamageResistance.TryGetValue(damageType, out var res) ? res : 0;
        int finalDamage = Math.Max(0, combatResult.Damage - dr);

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        return $"{action.ActionName}: Hit for {finalDamage} damage ({combatResult.Effects} Effects).{compMsg}";
    }

    protected override Task<string> ResolveContestedCheckAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        return Task.FromResult("Fallout 2d20: Contested checks are resolved as opposed skill tests. Needs implementation.");
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Fallout2d20Extension ?? new Fallout2d20Extension();
        int initiative = stats.Perception + stats.Agility;
        initiative = stats.ApplyModifiers("Initiative", initiative);
        
        // Add a lightweight roll to add variance instead of pure static stat
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = initiative, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        return outcome.Result;
    }
}
