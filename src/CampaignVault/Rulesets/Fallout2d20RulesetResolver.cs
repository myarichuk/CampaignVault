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

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid difficulty value '{diffStr}'.");
        }

        // Lower difficulty is better for the actor. Positive "SkillCheck" modifiers are good.
        // Fallout is TN based, so modifiers add to the TN, but if there's a difficulty modifier we could apply it here.
        // Let's modify the TN instead.
        
        var attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        var skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        var attrVal = GetAttributeValue(actorStats, attribute);
        var skillKey = actorStats.Skills.Keys.FirstOrDefault(k => string.Equals(k, skill, StringComparison.OrdinalIgnoreCase));
        var skillVal = skillKey != null && actorStats.Skills.TryGetValue(skillKey, out var s) ? s : 0;
        var targetNumber = attrVal + skillVal;
        
        targetNumber = ApplyAllModifiers(actorStats, targetNumber, "SkillCheck", skill, attribute);
        
        var isTagged = actorStats.TagSkills.Contains(skill);
        int? critThreshold = isTagged ? skillVal : null;
        
        var poolSize = 2;
        if (action.Parameters.TryGetValue("pool", out var p) && !int.TryParse(p, out poolSize))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid pool value '{p}'.");
        }

        var request = new RollRequest
        {
            Tag = "skill",
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
            CriticalThreshold = critThreshold
        };

        var outcome = await _rollService.RollAsync(request, ct);
        
        var success = outcome.Successes >= difficulty;
        var apGenerated = Math.Max(0, outcome.Successes - difficulty);
        var compMsg = outcome.HasComplication ? " COMPLICATION ROLLED!" : "";
        
        return ResolverResult.Ok($"{action.ActionName} ({attribute}+{skill} TN {targetNumber}): {(success ? "Success" : "Failure")}. Generated {apGenerated} AP.{compMsg} {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveAttackAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        if (target.SystemStats is not Fallout2d20Extension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var defense = targetStats.Defense;
        defense = ApplyAllModifiers(targetStats, defense, "Defense");
        
        var difficulty = defense;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid difficulty value '{diffStr}'.");
        }

        var attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Agility";
        var skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : "SmallGuns";
        
        var attrVal = GetAttributeValue(actorStats, attribute);
        var skillKey = actorStats.Skills.Keys.FirstOrDefault(k => string.Equals(k, skill, StringComparison.OrdinalIgnoreCase));
        var skillVal = skillKey != null && actorStats.Skills.TryGetValue(skillKey, out var s) ? s : 0;
        var targetNumber = attrVal + skillVal;
        
        targetNumber = ApplyAllModifiers(actorStats, targetNumber, "AttackRoll", skill, attribute);
        var isTagged = actorStats.TagSkills.Contains(skill);
        
        var poolSize = 2;
        if (action.Parameters.TryGetValue("pool", out var p) && !int.TryParse(p, out poolSize))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid pool value '{p}'.");
        }

        var request = new RollRequest
        {
            Tag = "attack",
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
            CriticalThreshold = isTagged ? skillVal : null
        };

        var outcome = await _rollService.RollAsync(request, ct);
        var success = outcome.Successes >= difficulty;
        var compMsg = outcome.HasComplication ? " COMPLICATION!" : "";

        if (!success)
        {
            return ResolverResult.Ok($"{action.ActionName}: Missed.{compMsg} {outcome.Summary}");
        }

        var combatDiceCount = 3;
        if (action.Parameters.TryGetValue("damageDice", out var cd) && !int.TryParse(cd, out combatDiceCount))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageDice value '{cd}'.");
        }

        combatDiceCount = ApplyAllModifiers(actorStats, combatDiceCount, "DamageRoll");
        var damageType = action.DamageType ?? (action.Parameters.TryGetValue("damageType", out var dt) ? dt : "Physical");

        var combatResult = await _rollService.RollFalloutCombatDiceAsync(combatDiceCount, ct);

        var drKey = targetStats.DamageResistance.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        var dr = drKey != null && targetStats.DamageResistance.TryGetValue(drKey, out var res) ? res : 0;
        var finalDamage = Math.Max(0, combatResult.Damage - dr);

        // Apply damage modifiers (resistances/vulnerabilities) multiplier
        var modKey = targetStats.DamageModifiers.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        if (modKey != null && targetStats.DamageModifiers.TryGetValue(modKey, out var multiplier))
        {
            finalDamage = (int)Math.Floor(finalDamage * multiplier);
        }

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        return ResolverResult.Ok($"{action.ActionName}: Hit for {finalDamage} damage ({combatResult.Effects} Effects).{compMsg}");
    }

    protected override Task<ResolverResult> ResolveContestedCheckAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        return Task.FromResult(ResolverResult.Fail("NotImplemented", "Fallout 2d20: Contested checks are resolved as opposed skill tests. Needs implementation."));
    }

    protected override async Task<ResolverResult> ResolveSavingThrowAsync(RulesetAction action, ChangeContext context, Fallout2d20Extension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid difficulty value '{diffStr}'.");
        }

        var attribute = action.Parameters.TryGetValue("attribute", out var attr) ? attr : "Endurance";
        var skill = action.Parameters.TryGetValue("skill", out var sk) ? sk : null;
        
        var attrVal = GetAttributeValue(actorStats, attribute);
        var skillKey = skill != null ? actorStats.Skills.Keys.FirstOrDefault(k => string.Equals(k, skill, StringComparison.OrdinalIgnoreCase)) : null;
        var skillVal = skillKey != null && actorStats.Skills.TryGetValue(skillKey, out var s) ? s : 0;
        var targetNumber = attrVal + skillVal;
        
        var tags = new List<string> { "SavingThrow", attribute };
        if (skill != null) tags.Add(skill);
        targetNumber = ApplyAllModifiers(actorStats, targetNumber, tags.ToArray());
        
        var isTagged = skill != null && actorStats.TagSkills.Contains(skill);
        int? critThreshold = isTagged ? skillVal : null;
        
        var poolSize = 2;
        if (action.Parameters.TryGetValue("pool", out var p) && !int.TryParse(p, out poolSize))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid pool value '{p}'.");
        }

        var request = new RollRequest
        {
            Tag = "save",
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
            CriticalThreshold = critThreshold
        };

        var outcome = await _rollService.RollAsync(request, ct);
        
        var success = outcome.Successes >= difficulty;
        var apGenerated = Math.Max(0, outcome.Successes - difficulty);
        var compMsg = outcome.HasComplication ? " COMPLICATION ROLLED!" : "";
        
        return ResolverResult.Ok($"{action.ActionName} ({attribute}{(skill != null ? "+" + skill : "")} TN {targetNumber}): {(success ? "Success" : "Failure")}. Generated {apGenerated} AP.{compMsg} {outcome.Summary}");
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Fallout2d20Extension ?? new Fallout2d20Extension();
        var initiative = stats.Perception + stats.Agility;
        initiative = ApplyAllModifiers(stats, initiative, "Initiative");
        
        // Add a lightweight roll to add variance instead of pure static stat
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = initiative, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        return outcome.Result;
    }
}
