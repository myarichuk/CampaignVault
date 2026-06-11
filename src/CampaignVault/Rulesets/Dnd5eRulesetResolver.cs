using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Contributors;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class Dnd5eRulesetResolver : RulesetResolverBase<Dnd5eExtension>
{
    private readonly IRollService _rollService;

    public Dnd5eRulesetResolver(IRollService rollService)
    {
        _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));
    }

    public override RulesetSystem System => RulesetSystem.Dnd5e;

    public override IEnumerable<IRulesetPressureContributor> PressureContributors =>
        [new Dnd5eExhaustionPressureContributor()];


    private int GetSkillOrAbilityBonus(Dnd5eExtension stats, string name)
    {
        var matchedKey = stats.SkillModifiers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (matchedKey != null && stats.SkillModifiers.TryGetValue(matchedKey, out var skillMod))
        {
            return skillMod;
        }

        return name.ToLower() switch
        {
            "strength" => stats.GetAbilityModifier(stats.Strength),
            "dexterity" => stats.GetAbilityModifier(stats.Dexterity),
            "constitution" => stats.GetAbilityModifier(stats.Constitution),
            "intelligence" => stats.GetAbilityModifier(stats.Intelligence),
            "wisdom" => stats.GetAbilityModifier(stats.Wisdom),
            "charisma" => stats.GetAbilityModifier(stats.Charisma),
            _ => 0
        };
    }

    private int GetSavingThrowBonus(Dnd5eExtension stats, string name)
    {
        var matchedKey = stats.SavingThrowModifiers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (matchedKey != null && stats.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
        {
            return saveMod;
        }

        return name.ToLower() switch
        {
            "strength" => stats.GetAbilityModifier(stats.Strength),
            "dexterity" => stats.GetAbilityModifier(stats.Dexterity),
            "constitution" => stats.GetAbilityModifier(stats.Constitution),
            "intelligence" => stats.GetAbilityModifier(stats.Intelligence),
            "wisdom" => stats.GetAbilityModifier(stats.Wisdom),
            "charisma" => stats.GetAbilityModifier(stats.Charisma),
            _ => 0
        };
    }

    protected override async Task<ResolverResult> ResolveAttackAsync(
        RulesetAction action, 
        ChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        if (target.SystemStats is not Dnd5eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var ac = targetStats.ArmorClass;
        ac = ApplyAllModifiers(targetStats, ac, "AC");
        
        // AC override
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
        {
            ac = overrideAc;
        }

        var attackBonus = 0;
        if (TryGetParameter(action.Parameters, out var b, "bonus", "toHitBonus") && !int.TryParse(b, out attackBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{b}'.");
        }

        attackBonus = ApplyAllModifiers(actorStats, attackBonus, "AttackRoll");

        var damageDice = action.Parameters.TryGetValue("damageDice", out var dd) ? dd : "1d4"; // Unarmed default
        
        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageBonus value '{db}'.");
        }

        damageBonus = ApplyAllModifiers(actorStats, damageBonus, "DamageRoll");

        var mechanic = GetMechanicFromAction(action);

        var attackRoll = await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = mechanic }, ct);
        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);

        var isHit = false;
        var isCrit = attackRoll.HasCritical; // Nat 20

        if (isCrit)
        {
            isHit = true;
        }
        else if (attackRoll.HasComplication)
        {
            isHit = false; // Nat 1
        }
        else if (attackRoll.Result >= ac)
        {
            isHit = true;
        }

        if (!isHit)
        {
            return ResolverResult.Ok($"{action.ActionName}: Missed. Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}");
        }

        // Handle critical damage (double dice)
        var finalDamage = damageRoll.Result;
        var critMsg = "";
        if (isCrit)
        {
            // Per D&D 5e PHB: "Roll all of the attack's damage dice twice and add them together. Then add any relevant modifiers as normal."
            // Since we already rolled `damageRoll` once (which included the modifier), we roll the pure dice again and add it.
            var critDmg = await _rollService.RollAsync(new RollRequest { Tag = "critDamage", Expression = damageDice, Mechanic = DiceMechanic.Standard }, ct);
            finalDamage += critDmg.Result;
            critMsg = $" CRITICAL HIT! Added {critDmg.Result} extra damage.";
        }

        // Apply damage modifiers (resistances/vulnerabilities)
        var damageType = action.DamageType ?? "Physical";
        if (targetStats.DamageModifiers.TryGetValue(damageType, out var multiplier))
        {
            finalDamage = (int)Math.Floor(finalDamage * multiplier);
        }

        mutations.Add(new HpChange
        {
            CharacterId = targetId,
            Delta = -finalDamage
        });

        return ResolverResult.Ok($"{action.ActionName}: Hit for {finalDamage} damage. (Attack {attackRoll.Result} vs AC {ac}).{critMsg}");
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(
        RulesetAction action, 
        ChangeContext context,
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Skill check requires a 'dc' parameter.");
        }

        var skillName = action.Parameters.TryGetValue("skill", out var s) ? s : "Strength";
        var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        bonus = ApplyAllModifiers(actorStats, bonus, "SkillCheck", skillName);
        var mechanic = GetMechanicFromAction(action);

        var outcome = await _rollService.RollAsync(new RollRequest
        {
            Tag = "skill",
            Expression = "1d20",
            Bonus = bonus,
            Mechanic = mechanic
        }, ct);

        var isSuccess = outcome.Result >= dc;
        var resultStr = isSuccess ? "Success" : "Failure";
        
        return ResolverResult.Ok($"{action.ActionName} ({skillName}): {resultStr}. Rolled {outcome.Result} vs DC {dc}. {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action, 
        ChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for contested check.");
        }

        if (target.SystemStats is not Dnd5eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var isGrapple = EngagementMutationHelper.IsGrappleAction(action);
        var isEscape = EngagementMutationHelper.IsEscapeGrappleAction(action);

        var actorSkill = action.Parameters.TryGetValue("skill", out var as_name)
            ? as_name
            : isGrapple || isEscape ? "Athletics" : "Strength";
        var targetSkill = action.Parameters.TryGetValue("targetSkill", out var ts_name)
            ? ts_name
            : isGrapple ? "Athletics" : actorSkill;

        var actorBonus = GetSkillOrAbilityBonus(actorStats, actorSkill);
        actorBonus = ApplyAllModifiers(actorStats, actorBonus, "SkillCheck", actorSkill);

        var targetBonus = GetSkillOrAbilityBonus(targetStats, targetSkill);
        targetBonus = ApplyAllModifiers(targetStats, targetBonus, "SkillCheck", targetSkill);

        var actorRoll = await _rollService.RollAsync(new RollRequest { Tag = "actor", Expression = "1d20", Bonus = actorBonus, Mechanic = GetMechanicFromAction(action) }, ct);
        var targetRoll = await _rollService.RollAsync(new RollRequest { Tag = "target", Expression = "1d20", Bonus = targetBonus, Mechanic = DiceMechanic.Standard }, ct);

        var actorWins = actorRoll.Result > targetRoll.Result;
        var resultStr = actorWins ? "Actor Wins" : "Target Wins";

        if (isGrapple && actorWins)
        {
            EngagementMutationHelper.ApplyGrappleSuccess(action.ActorId, targetId, mutations);
            resultStr += " Target is now grappled.";
        }
        else if (isEscape && actorWins)
        {
            EngagementMutationHelper.ApplyGrappleEscape(action.ActorId, targetId, mutations);
            resultStr += " Actor breaks free of the grapple.";
        }

        return ResolverResult.Ok($"{action.ActionName}: {resultStr}. Actor rolled {actorRoll.Result} ({actorSkill}), Target rolled {targetRoll.Result} ({targetSkill}).");
    }

    protected override async Task<ResolverResult> ResolveSavingThrowAsync(
        RulesetAction action, 
        ChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Saving throw requires a 'dc' parameter.");
        }

        var saveName = action.Parameters.TryGetValue("save", out var s) ? s : "Dexterity";
        var bonus = GetSavingThrowBonus(actorStats, saveName);
        
        bonus = ApplyAllModifiers(actorStats, bonus, "SavingThrow", saveName);
        var mechanic = GetMechanicFromAction(action);

        var outcome = await _rollService.RollAsync(new RollRequest
        {
            Tag = "save",
            Expression = "1d20",
            Bonus = bonus,
            Mechanic = mechanic
        }, ct);

        var isSuccess = outcome.Result >= dc;
        var resultStr = isSuccess ? "Success" : "Failure";
        
        return ResolverResult.Ok($"{action.ActionName} ({saveName} Save): {resultStr}. Rolled {outcome.Result} vs DC {dc}. {outcome.Summary}");
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Dnd5eExtension ?? new Dnd5eExtension();
        var dexMod = stats.GetAbilityModifier(stats.Dexterity);
        dexMod = ApplyAllModifiers(stats, dexMod, "Initiative");
        
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = dexMod, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        
        // Use result + bonus as secondary tie-breaker (e.g. 15 roll + 2 mod = 15.02)
        // Helps D&D's "dexterity breaks ties" rule slightly without complex structures.
        return outcome.Result + (dexMod * 0.01f);
    }
}
