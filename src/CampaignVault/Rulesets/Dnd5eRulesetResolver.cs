using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Contributors;

namespace CampaignVault.Rulesets;

public class Dnd5eRulesetResolver : RulesetResolverBase<Dnd5eExtension>
{
    private readonly IRollService _rollService;
    private readonly ICharacterBootstrapPipeline _bootstrap;

    public Dnd5eRulesetResolver(IRollService rollService)
    {
        _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));
        var hpStep = new Dnd5eDeriveHitPointsStep(_rollService);
        var profStep = new Dnd5eDeriveProficiencyStep();
        var passiveStep = new Dnd5eDerivePassivePerceptionStep();
        var spellStep = new Dnd5eDeriveSpellcastingStep();
        _bootstrap = new CharacterBootstrapPipeline(
        [
            hpStep,
            new Dnd5eDeriveDefenseStep(),
            profStep,
            passiveStep,
            spellStep,
        ],
        [hpStep, profStep, passiveStep, spellStep]);
    }

    public override RulesetSystem System => RulesetSystem.Dnd5e;

    public override ICharacterBootstrapPipeline Bootstrap => _bootstrap;

    public override IEnumerable<IRulesetPressureContributor> PressureContributors =>
        [new Dnd5eExhaustionPressureContributor()];

    protected override IRollService? GetRollService() => _rollService;

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
        var targets = AttackTargetHelper.SelectTargets(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        var narratives = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var result = await ResolveAttackAgainstTargetAsync(
                action, targets[i], context, actorStats, mutations, ct);
            if (!result.Success)
            {
                return result;
            }

            narratives.Add(result.Narrative);
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    private async Task<ResolverResult> ResolveAttackAgainstTargetAsync(
        RulesetAction action,
        string targetId,
        ChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for attack.");
        }

        if (target.SystemStats is not Dnd5eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var ac = targetStats.ArmorClass;
        ac = ApplyAllModifiers(targetStats, ac, "AC");
        
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
        {
            ac = overrideAc;
        }

        var attackBonus = 0;
        var hasExplicitBonus = TryGetParameter(action.Parameters, out var b, "bonus", "toHitBonus", "spellAttackBonus");
        if (hasExplicitBonus && !int.TryParse(b, out attackBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{b}'.");
        }

        if (!hasExplicitBonus && action.ActionType == RulesetActionType.Spell)
        {
            attackBonus = ResolveSpellAttackBonus(actorStats);
        }

        attackBonus = ApplyAllModifiers(actorStats, attackBonus, "AttackRoll");

        var damageDice = action.Parameters.GetValueOrDefault("damageDice", "1d4");
        
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
        var isCrit = attackRoll.HasCritical;

        if (isCrit)
        {
            isHit = true;
        }
        else if (attackRoll.HasComplication)
        {
            isHit = false;
        }
        else if (attackRoll.Result >= ac)
        {
            isHit = true;
        }

        if (!isHit)
        {
            return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Missed. Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}");
        }

        var finalDamage = damageRoll.Result;
        var critMsg = "";
        if (isCrit)
        {
            var critDmg = await _rollService.RollAsync(new RollRequest { Tag = "critDamage", Expression = damageDice, Mechanic = DiceMechanic.Standard }, ct);
            finalDamage += critDmg.Result;
            critMsg = $" CRITICAL HIT! Added {critDmg.Result} extra damage.";
        }

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

        return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Hit for {finalDamage} damage. (Attack {attackRoll.Result} vs AC {ac}).{critMsg}");
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

        var skillName = action.Parameters.GetValueOrDefault("skill", "Strength");
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
            EngagementMutationHelper.ApplyGrappleSuccess(action.CharacterId, targetId, mutations);
            resultStr += " Target is now grappled.";
        }
        else if (isEscape && actorWins)
        {
            EngagementMutationHelper.ApplyGrappleEscape(action.CharacterId, targetId, mutations);
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

        var saveName = action.Parameters.GetValueOrDefault("save", "Dexterity");
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
        
        var damageApplied = await TryApplySaveDamageAsync(
            action, action.CharacterId, isSuccess, mutations, _rollService, ct);
        var damageMsg = damageApplied > 0
            ? $" Took {damageApplied} damage."
            : string.Empty;

        return ResolverResult.Ok(
            $"{action.ActionName} ({saveName} Save): {resultStr}. Rolled {outcome.Result} vs DC {dc}.{damageMsg} {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveSpellSaveAsync(
        RulesetAction action,
        ChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targets = AttackTargetHelper.SelectTargets(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: Spell save requires at least one target in targetIds.");
        }

        var dc = ResolveSpellSaveDc(actorStats, action);
        if (dc <= 0)
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Spell save requires a 'dc' parameter or spellSaveDc on the caster.");
        }

        var saveName = action.Parameters.GetValueOrDefault("save", "Dexterity");
        var narratives = new List<string>();

        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for spell save.");
            }

            if (target.SystemStats is not Dnd5eExtension targetStats)
            {
                return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
            }

            var bonus = GetSavingThrowBonus(targetStats, saveName);
            bonus = ApplyAllModifiers(targetStats, bonus, "SavingThrow", saveName);
            var mechanic = GetMechanicFromAction(action);

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "spell-save",
                Expression = "1d20",
                Bonus = bonus,
                Mechanic = mechanic,
            }, ct);

            var isSuccess = outcome.Result >= dc;
            var damage = await TryApplySaveDamageAsync(action, targetId, isSuccess, mutations, _rollService, ct);
            narratives.Add(
                $"{action.ActionName} vs {target.Name}: {(isSuccess ? "Saved" : "Failed")} ({saveName} {outcome.Result} vs DC {dc})"
                + (damage > 0 ? $" — {damage} damage." : "."));
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    protected override async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        ChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.ContainsKey("dc"))
        {
            return ResolverResult.Ok(
                $"{action.ActionName}: Utility spell cast outside combat. Narrate scouting, communication, or ward effects; commit status or knowledge_update if the scene changes.");
        }

        var skillName = action.Parameters.TryGetValue("skill", out var skill)
            ? skill
            : actorStats.SpellcastingAbility ?? "Arcana";
        action.Parameters["skill"] = skillName;
        return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
    }

    private int ResolveSpellSaveDc(Dnd5eExtension actorStats, RulesetAction action)
    {
        if (action.Parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var explicitDc))
        {
            return explicitDc;
        }

        var level = actorStats.Level ?? 1;
        var proficiency = Dnd5eClassProfileResolver.ProficiencyBonus(level);
        var ability = actorStats.SpellcastingAbility
            ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(actorStats.ClassLevels)
            ?? "Intelligence";
        return Dnd5eSpellcastingHelper.ComputeSpellSaveDc(actorStats, proficiency, ability);
    }

    private int ResolveSpellAttackBonus(Dnd5eExtension actorStats)
    {
        if (actorStats.SpellAttackBonus is int bonus)
        {
            return bonus;
        }

        var level = actorStats.Level ?? 1;
        var proficiency = Dnd5eClassProfileResolver.ProficiencyBonus(level);
        var ability = actorStats.SpellcastingAbility
            ?? Dnd5eSpellcastingHelper.InferSpellcastingAbility(actorStats.ClassLevels)
            ?? "Intelligence";
        return Dnd5eSpellcastingHelper.ComputeSpellAttackBonus(actorStats, proficiency, ability);
    }

    private static async Task<int> TryApplySaveDamageAsync(
        RulesetAction action,
        string targetId,
        bool saved,
        List<WorldChange> mutations,
        IRollService rollService,
        CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("damageDice", out var damageDice))
        {
            return 0;
        }

        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db))
        {
            int.TryParse(db, out damageBonus);
        }

        var damageRoll = await rollService.RollAsync(new RollRequest
        {
            Tag = "spell-damage",
            Expression = damageDice,
            Bonus = damageBonus,
            Mechanic = DiceMechanic.Standard,
        }, ct);

        var halfOnSave = !action.Parameters.TryGetValue("halfOnSave", out var halfStr)
            || halfStr.Equals("true", StringComparison.OrdinalIgnoreCase)
            || halfStr == "1";

        var damage = saved && halfOnSave
            ? (int)Math.Floor(damageRoll.Result / 2.0)
            : saved ? 0 : damageRoll.Result;

        if (damage > 0)
        {
            mutations.Add(new HpChange { CharacterId = targetId, Delta = -damage });
        }

        return damage;
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
