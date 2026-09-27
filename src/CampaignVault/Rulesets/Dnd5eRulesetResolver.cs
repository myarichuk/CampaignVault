using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Contributors;
using CampaignVault.Services;
using RegularExpressions = System.Text.RegularExpressions;

namespace CampaignVault.Rulesets;

public class Dnd5eRulesetResolver : RulesetResolverBase<Dnd5eExtension>
{
    private readonly IRollService _rollService;
    private readonly ICharacterBootstrapPipeline _bootstrap;
    private readonly SpellDefinitionProvider? _spellDefinitionProvider;

    public Dnd5eRulesetResolver(
        IRollService rollService,
        RaceDefinitionProvider? raceProvider = null,
        ClassDefinitionProvider? classProvider = null,
        BackgroundDefinitionProvider? backgroundProvider = null,
        SpellDefinitionProvider? spellDefinitionProvider = null)
    {
        _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));
        _spellDefinitionProvider = spellDefinitionProvider;
        var hpStep = new Dnd5eDeriveHitPointsStep(_rollService);
        var profStep = new Dnd5eDeriveProficiencyStep(classProvider, backgroundProvider);
        var passiveStep = new Dnd5eDerivePassivePerceptionStep();
        var spellStep = new Dnd5eDeriveSpellcastingStep();
        List<IBootstrapStep> steps = raceProvider != null ? [new Dnd5eDeriveRaceStep(raceProvider)] : [];
        steps.AddRange([hpStep, new Dnd5eDeriveDefenseStep(), profStep, passiveStep, spellStep]);
        _bootstrap = new CharacterBootstrapPipeline(
            steps,
            [hpStep, profStep, passiveStep, spellStep]);
    }

    public override string System => RulesetSystem.Dnd5e;

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
        IChangeContext context, 
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
        IChangeContext context,
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

        var drKey = targetStats.DamageResistances.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        var flatDr = drKey != null && targetStats.DamageResistances.TryGetValue(drKey, out var dr) ? dr : 0;

        if (targetStats.DamageModifiers.TryGetValue(damageType, out var multiplier))
        {
            finalDamage = (int)Math.Floor(finalDamage * multiplier);
        }

        finalDamage = Math.Max(0, finalDamage - flatDr);

        mutations.Add(new HpChange
        {
            CharacterId = targetId,
            Delta = -finalDamage
        });

        var damageWarning = BuildSpellDamageWarning(action, damageDice, actorStats.Level);

        return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Hit for {finalDamage} damage. (Attack {attackRoll.Result} vs AC {ac}).{critMsg}{damageWarning}");
    }

    /// <summary>
    /// Ability abbreviation → full name, as dnd5eapi.co's spell "dc.dc_type.index" carries it (e.g.
    /// "dex"), for comparing against the caller's ruleset_action.parameters.save (e.g. "Dexterity").
    /// </summary>
    private static readonly Dictionary<string, string> AbilityAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["str"] = "Strength",
        ["dex"] = "Dexterity",
        ["con"] = "Constitution",
        ["int"] = "Intelligence",
        ["wis"] = "Wisdom",
        ["cha"] = "Charisma",
    };

    private static string NormalizeSpellSlug(string name) =>
        name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private static string NormalizeDice(string dice) =>
        RegularExpressions.Regex.Replace(dice.Trim(), @"\s+", "");

    /// <summary>
    /// Nothing in the engine used to validate ruleset_action.parameters.damageDice against the
    /// caster's actual level or spell-slot tier, so an LLM caller guessing the wrong scaling tier
    /// (e.g. "3d10" — the level 11-16 tier for Fire Bolt — sent by a level 1 caster) rolled and
    /// applied real, unvalidated damage with a narrative that reads as entirely correct
    /// ("Attack 27 vs AC 12"). Now backed by dnd5eapi.co's real per-spell data (CONTENT_GAPS_PLAN.md
    /// Step 3) instead of a 4-entry hardcoded cantrip table. Soft warning only, consistent with
    /// SpellSlotValidator's CantripWarning — damage still applies as sent, since a homebrew/plugin
    /// spell or an intentional reflavor has no SpellDefinition to check against, and this must not
    /// block real play. dnd5e only (pf2e spell damage is prose-only on AoN — see Step 2 findings).
    /// Leveled (spell-slot-scaling) spells can't be pinned to an exact tier here: the resolver has
    /// no reliable signal for which slot level the caller spent (that's tracked by a separate
    /// ResourceChange, not this RulesetAction), so those are checked against "matches any known
    /// tier" rather than "matches the caster's specific level" the way cantrips are.
    /// </summary>
    private string BuildSpellDamageWarning(RulesetAction action, string damageDice, int? casterLevel)
    {
        if (action.ActionType != RulesetActionType.Spell || _spellDefinitionProvider == null)
        {
            return "";
        }

        var slug = NormalizeSpellSlug(action.ActionName);
        if (!_spellDefinitionProvider.TryGet(System, slug, out var spell) || spell == null)
        {
            return "";
        }

        var normalizedDice = NormalizeDice(damageDice);

        if ((spell.Level ?? -1) == 0 && spell.DamageAtCharacterLevel is { Count: > 0 } byCharacterLevel)
        {
            var tier = byCharacterLevel.Keys.Where(l => l <= (casterLevel ?? 1)).DefaultIfEmpty(byCharacterLevel.Keys.Min()).Max();
            var expected = byCharacterLevel[tier];
            if (!string.Equals(NormalizeDice(expected), normalizedDice, StringComparison.OrdinalIgnoreCase))
            {
                return $" [WARNING] '{action.ActionName}' at caster level {casterLevel ?? 1} should deal " +
                       $"{expected}, not {damageDice} — damage was applied as sent, but check the cantrip-scaling tier.";
            }
        }
        else if (spell.DamageAtSlotLevel is { Count: > 0 } bySlotLevel)
        {
            if (!bySlotLevel.Values.Any(v => string.Equals(NormalizeDice(v), normalizedDice, StringComparison.OrdinalIgnoreCase)))
            {
                var known = string.Join(", ", bySlotLevel.OrderBy(kv => kv.Key).Select(kv => $"slot {kv.Key}: {kv.Value}"));
                return $" [WARNING] '{action.ActionName}' damageDice '{damageDice}' doesn't match any known spell-slot tier ({known}) " +
                       "— damage was applied as sent, but check the slot level cast.";
            }
        }

        return "";
    }

    /// <summary>dnd5e only, mirrors BuildSpellDamageWarning's soft-warning approach for the saving-throw ability.</summary>
    private string BuildSpellSaveTypeWarning(RulesetAction action, string saveName)
    {
        if (action.ActionType != RulesetActionType.Spell || _spellDefinitionProvider == null)
        {
            return "";
        }

        var slug = NormalizeSpellSlug(action.ActionName);
        if (!_spellDefinitionProvider.TryGet(System, slug, out var spell) || string.IsNullOrEmpty(spell?.SaveType))
        {
            return "";
        }

        var expectedAbility = AbilityAbbreviations.GetValueOrDefault(spell.SaveType.Trim(), spell.SaveType);
        if (!string.Equals(expectedAbility, saveName, StringComparison.OrdinalIgnoreCase))
        {
            return $" [WARNING] '{action.ActionName}' should use a {expectedAbility} save, not {saveName} " +
                   "— save was resolved as sent, but check the saving-throw ability.";
        }

        return "";
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(
        RulesetAction action,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Skill check requires a 'dc' parameter.");
        }

        var skillName = action.Parameters.GetValueOrDefault("skill", action.ActionName);
        var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        bonus = ApplyAllModifiers(actorStats, bonus, "SkillCheck", skillName);

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (SocialSkillGating.ShouldApplyRelationshipModifier(System, action, skillName))
        {
            var targetId = SocialSkillGating.ResolveRelationshipTargetId(action);
            if (targetId != null &&
                context.Characters.TryGetValue(targetId, out var target) &&
                context.Characters.TryGetValue(action.CharacterId, out var actor))
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(
                    target, actor, CampaignConfigHelper.EffectiveConfig(context));
                bonus += relationshipBonus;
            }
        }

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
        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";

        return new ResolverResult
        {
            Narrative = $"{action.ActionName} ({skillName}): {resultStr}. Rolled {outcome.Result} vs DC {dc}.{relationshipSuffix} {outcome.Summary}",
            RollTotal = outcome.Result,
            Skill = skillName
        };
    }

    protected override async Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action, 
        IChangeContext context, 
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

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (!isGrapple && !isEscape && SocialSkillGating.ShouldApplyRelationshipModifier(System, action, actorSkill))
        {
            if (context.Characters.TryGetValue(action.CharacterId, out var actor))
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(
                    target, actor, CampaignConfigHelper.EffectiveConfig(context));
                actorBonus += relationshipBonus;
            }
        }

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

        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";
        return ResolverResult.Ok($"{action.ActionName}: {resultStr}. Actor rolled {actorRoll.Result} ({actorSkill}){relationshipSuffix}, Target rolled {targetRoll.Result} ({targetSkill}).");
    }

    protected override async Task<ResolverResult> ResolveSavingThrowAsync(
        RulesetAction action, 
        IChangeContext context, 
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
        IChangeContext context,
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

        var saveTypeWarning = BuildSpellSaveTypeWarning(action, saveName);
        var damageWarning = action.Parameters.TryGetValue("damageDice", out var saveDamageDice)
            ? BuildSpellDamageWarning(action, saveDamageDice, actorStats.Level)
            : "";

        return ResolverResult.Ok(string.Join(" | ", narratives) + saveTypeWarning + damageWarning);
    }

    protected override async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        IChangeContext context,
        Dnd5eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.ContainsKey("dc"))
        {
            // F1: a targeted spell with no mechanics (e.g. Fire Bolt with targetIds and no
            // parameters) used to resolve as a utility no-op — no roll, but the action was
            // still consumed. Fail with a fix hint instead of silently doing nothing.
            // Only when Utility was inferred: an explicit resolution=utility opts out
            // (e.g. a buff whose status the caller commits separately in the same batch).
            if (action.TargetIds.Count > 0
                && !action.Parameters.ContainsKey("resolution")
                && !action.Parameters.ContainsKey("spellResolution"))
            {
                return ResolverResult.Fail("InvalidParameter",
                    $"Error: {action.ActionName} targets {action.TargetIds.Count} character(s) but supplies no spell mechanics, " +
                    "so there is nothing to resolve. Fix: pass the spell's mechanics explicitly — damageDice plus " +
                    "toHitBonus (or resolution=attack) for an attack roll like Fire Bolt, save + dc (plus damageDice) " +
                    "for a saving-throw spell, or resolution=utility for a genuine non-damaging effect (which needs no targets).");
            }

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
        
        // Use result + bonus as secondary tie-breaker (e.g. 15 roll + 2 mod = 15.1)
        // Widens dex-mod scaling to meaningfully break ties without fully dominating roll order
        return outcome.Result + (dexMod / 20f);
    }
}
