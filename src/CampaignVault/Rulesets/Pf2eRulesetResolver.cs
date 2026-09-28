using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

public enum Pf2eDegreeOfSuccess
{
    CriticalFailure,
    Failure,
    Success,
    CriticalSuccess
}

public class Pf2eRulesetResolver : RulesetResolverBase<Pf2eExtension>
{
    private readonly IRollService _rollService;
    private readonly ICharacterBootstrapPipeline _bootstrap;
    private readonly SpellDefinitionProvider? _spellDefinitionProvider;
    private readonly CreatureDefinitionProvider? _creatureDefinitionProvider;

    public Pf2eRulesetResolver(
        IRollService rollService,
        RaceDefinitionProvider? raceProvider = null,
        SpellDefinitionProvider? spellDefinitionProvider = null,
        CreatureDefinitionProvider? creatureDefinitionProvider = null,
        RollModifierPipeline? rollModifiers = null)
    {
        if (rollModifiers is not null)
            Pipeline = rollModifiers;
        _rollService = rollService;
        _spellDefinitionProvider = spellDefinitionProvider;
        _creatureDefinitionProvider = creatureDefinitionProvider;
        var hpStep = new Pf2eDeriveHitPointsStep();
        var profStep = new Pf2eDeriveProficiencyStep();
        var spellStep = new Pf2eDeriveSpellcastingStep();
        List<IBootstrapStep> steps = raceProvider != null ? [new Pf2eDeriveAncestryStep(raceProvider)] : [];
        steps.AddRange([hpStep, new Pf2eDeriveDefenseStep(), profStep, spellStep]);
        _bootstrap = new CharacterBootstrapPipeline(
            steps,
            [hpStep, profStep, spellStep]);
    }

    public override string System => RulesetSystem.Pathfinder2e;

    public override ICharacterBootstrapPipeline Bootstrap => _bootstrap;

    protected override SpellDefinition? LookupSummonSpell(RulesetAction action)
    {
        if (_spellDefinitionProvider == null)
        {
            return null;
        }

        var slug = NormalizeSpellSlug(action.ActionName);
        if (!_spellDefinitionProvider.TryGet(System, slug, out var spell) || spell?.Summon == null)
        {
            return null;
        }

        return spell;
    }

    protected override CreatureDefinitionProvider? SummonCreatureProvider => _creatureDefinitionProvider;

    protected override int MaxSummonSlot => 10;

    protected override void ApplySummonedDefense(Pf2eExtension stats, int defense) =>
        stats.ArmorClass = defense;

    protected override IRollService? GetRollService() => _rollService;

    private int GetSkillOrAbilityBonus(Pf2eExtension stats, string name)
    {
        var matchedKey = stats.SkillModifiers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (matchedKey != null && stats.SkillModifiers.TryGetValue(matchedKey, out var skillMod))
        {
            return skillMod;
        }

        return name.ToLower() switch
        {
            "strength" => stats.StrengthMod,
            "dexterity" => stats.DexterityMod,
            "constitution" => stats.ConstitutionMod,
            "intelligence" => stats.IntelligenceMod,
            "wisdom" => stats.WisdomMod,
            "charisma" => stats.CharismaMod,
            _ => 0
        };
    }

    private int GetSavingThrowBonus(Pf2eExtension stats, string name)
    {
        var matchedKey = stats.SavingThrowModifiers.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (matchedKey != null && stats.SavingThrowModifiers.TryGetValue(matchedKey, out var saveMod))
        {
            return saveMod;
        }

        return name.ToLower() switch
        {
            "fortitude" => stats.ConstitutionMod,
            "reflex" => stats.DexterityMod,
            "will" => stats.WisdomMod,
            "strength" => stats.StrengthMod,
            "dexterity" => stats.DexterityMod,
            "constitution" => stats.ConstitutionMod,
            "intelligence" => stats.IntelligenceMod,
            "wisdom" => stats.WisdomMod,
            "charisma" => stats.CharismaMod,
            _ => 0
        };
    }

    /// <summary>
    /// Resolves the DC for a check: an explicit 'dc' parameter always wins. For Spell actions with
    /// no explicit dc, falls back to the actor's bootstrap-derived SpellDc so casters don't have to
    /// pass a DC the engine already computed. SkillCheck/SavingThrow DCs are inherently GM-set and
    /// have no derivable fallback, so they still hard-fail without an explicit dc.
    /// </summary>
    private static bool TryResolveDc(RulesetAction action, Pf2eExtension actorStats, string context, out int dc, out string? error)
    {
        if (action.Parameters.TryGetValue("dc", out var dcStr))
        {
            if (int.TryParse(dcStr, out dc))
            {
                error = null;
                return true;
            }

            dc = 0;
            error = $"Error: invalid dc value '{dcStr}'.";
            return false;
        }

        if (action.ActionType == RulesetActionType.Spell && actorStats.SpellDc.HasValue)
        {
            dc = actorStats.SpellDc.Value;
            error = null;
            return true;
        }

        dc = 0;
        error = $"Error: {context} requires a 'dc' parameter" +
            (action.ActionType == RulesetActionType.Spell
                ? " (no derived spellDc — ensure spellcastingAbility and level are bootstrapped for this caster, or pass dc explicitly)."
                : ".");
        return false;
    }

    private Pf2eDegreeOfSuccess CalculateDegreeOfSuccess(RollOutcome roll, int dc)
    {
        Pf2eDegreeOfSuccess degree;
        var margin = roll.Result - dc;

        if (margin >= 10)
        {
            degree = Pf2eDegreeOfSuccess.CriticalSuccess;
        }
        else if (margin >= 0)
        {
            degree = Pf2eDegreeOfSuccess.Success;
        }
        else if (margin <= -10)
        {
            degree = Pf2eDegreeOfSuccess.CriticalFailure;
        }
        else
        {
            degree = Pf2eDegreeOfSuccess.Failure;
        }

        if (roll.HasCritical) // Nat 20 upgrades
        {
            if (degree != Pf2eDegreeOfSuccess.CriticalSuccess)
            {
                degree = (Pf2eDegreeOfSuccess)((int)degree + 1);
            }
        }
        else if (roll.HasComplication) // Nat 1 downgrades
        {
            if (degree != Pf2eDegreeOfSuccess.CriticalFailure)
            {
                degree = (Pf2eDegreeOfSuccess)((int)degree - 1);
            }
        }

        return degree;
    }

    protected override async Task<ResolverResult> ResolveAttackAsync(RulesetAction action, IChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var targets = AttackTargetHelper.SelectAttackInstances(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        var narratives = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var result = await ResolveAttackAgainstTargetAsync(
                action, targets[i], context, actorStats, mutations, i, ct);
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
        Pf2eExtension actorStats,
        List<WorldChange> mutations,
        int attackIndex,
        CancellationToken ct)
    {
        if (!context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for attack.");
        }

        if (target.SystemStats is not Pf2eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var ac = targetStats.ArmorClass;
        ac = (await FoldAsync(context, target, RollKinds.ArmorClass, null, ac, action).ConfigureAwait(false)).Bonus;
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
        {
            ac = overrideAc;
        }

        var attackBonus = 0;
        if (action.Parameters.TryGetValue("bonus", out var b) && !int.TryParse(b, out attackBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{b}'.");
        }

        if (action.Parameters.TryGetValue("mapPenalty", out var mapStr))
        {
            if (int.TryParse(mapStr, out var mapVal))
            {
                attackBonus -= Math.Abs(mapVal);
            }
            else
            {
                return ResolverResult.Fail("InvalidParameter", $"Error: invalid mapPenalty value '{mapStr}'.");
            }
        }
        else if (attackIndex > 0)
        {
            attackBonus -= attackIndex * 5;
        }

        var attackFold = await FoldAsync(
            context, context.Characters[action.CharacterId], RollKinds.Attack, null, attackBonus, action, GetMechanicFromAction(action), target).ConfigureAwait(false);
        attackBonus = attackFold.Bonus;

        var damageDice = action.Parameters.GetValueOrDefault("damageDice", "1d4");
        
        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageBonus value '{db}'.");
        }

        damageBonus = (await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Damage, null, damageBonus, action).ConfigureAwait(false)).Bonus;

        var attackRoll = await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = attackFold.Mechanic }, ct);
        
        var degree = CalculateDegreeOfSuccess(attackRoll, ac);

        if (degree == Pf2eDegreeOfSuccess.Failure || degree == Pf2eDegreeOfSuccess.CriticalFailure)
        {
            return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Missed. ({degree}) Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}");
        }

        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);
        var finalDamage = damageRoll.Result;

        if (degree == Pf2eDegreeOfSuccess.CriticalSuccess)
        {
            finalDamage *= 2;
        }

        var damageType = action.DamageType ?? "Physical";

        var drKey = targetStats.DamageResistances.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        var flatDr = drKey != null && targetStats.DamageResistances.TryGetValue(drKey, out var dr) ? dr : 0;

        if (targetStats.DamageModifiers.TryGetValue(damageType, out var multiplier))
        {
            finalDamage = (int)Math.Floor(finalDamage * multiplier);
        }

        finalDamage = Math.Max(0, finalDamage - flatDr);

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Hit for {finalDamage} damage. ({degree}) Attack {attackRoll.Result} vs AC {ac}.");
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(RulesetAction action, IChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        if (!TryResolveDc(action, actorStats, "Skill check", out var dc, out var dcError))
        {
            return ResolverResult.Fail("InvalidParameter", dcError!);
        }

        var skillName = action.Parameters.GetValueOrDefault("skill", action.ActionName);
        var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        bonus = (await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, skillName, bonus, action).ConfigureAwait(false)).Bonus;

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (SocialSkillGating.ShouldApplyRelationshipModifier(System, action, skillName))
        {
            var targetId = SocialSkillGating.ResolveRelationshipTargetId(action);
            if (targetId != null && context.Characters.TryGetValue(targetId, out var target) &&
                context.Characters.TryGetValue(action.CharacterId, out var actor))
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(
                    target, actor, CampaignConfigHelper.EffectiveConfig(context));
                bonus += relationshipBonus;
            }
        }

        var outcome = await _rollService.RollAsync(new RollRequest { Tag = "skill", Expression = "1d20", Bonus = bonus, Mechanic = GetMechanicFromAction(action) }, ct);

        var degree = CalculateDegreeOfSuccess(outcome, dc);
        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";

        return new ResolverResult
        {
            Narrative = $"{action.ActionName} ({skillName}): {degree}. Rolled {outcome.Result} vs DC {dc}.{relationshipSuffix} {outcome.Summary}",
            RollTotal = outcome.Result,
            Skill = skillName
        };
    }

    protected override async Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action,
        IChangeContext context,
        Pf2eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for contested check.");
        }

        if (target.SystemStats is not Pf2eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var isGrapple = EngagementMutationHelper.IsGrappleAction(action);
        var isEscape = EngagementMutationHelper.IsEscapeGrappleAction(action);

        if (isGrapple)
        {
            var skillName = action.Parameters.GetValueOrDefault("skill", "Athletics");
            var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
            bonus = (await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, skillName, bonus, action).ConfigureAwait(false)).Bonus;

            var fortDc = 10 + GetSavingThrowBonus(targetStats, "Fortitude");
            if (action.Parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var overrideDc))
            {
                fortDc = overrideDc;
            }

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "grapple",
                Expression = "1d20",
                Bonus = bonus,
                Mechanic = GetMechanicFromAction(action)
            }, ct);

            var degree = CalculateDegreeOfSuccess(outcome, fortDc);
            var success = degree is Pf2eDegreeOfSuccess.Success or Pf2eDegreeOfSuccess.CriticalSuccess;

            if (success)
            {
                EngagementMutationHelper.ApplyGrappleSuccess(action.CharacterId, targetId, mutations);
            }

            var resultSuffix = success ? " Target is now grabbed." : string.Empty;
            return ResolverResult.Ok($"{action.ActionName}: {degree}. Rolled {outcome.Result} vs Fortitude DC {fortDc}.{resultSuffix} {outcome.Summary}");
        }

        if (isEscape)
        {
            var skillName = action.Parameters.GetValueOrDefault("skill", "Athletics");
            var actorBonus = GetSkillOrAbilityBonus(actorStats, skillName);
            actorBonus = (await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, skillName, actorBonus, action).ConfigureAwait(false)).Bonus;

            var grapplerBonus = GetSkillOrAbilityBonus(targetStats, skillName);
            grapplerBonus = (await FoldAsync(context, target, RollKinds.Check, skillName, grapplerBonus, action).ConfigureAwait(false)).Bonus;
            var escapeDc = 10 + grapplerBonus;

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "escape",
                Expression = "1d20",
                Bonus = actorBonus,
                Mechanic = GetMechanicFromAction(action)
            }, ct);

            var degree = CalculateDegreeOfSuccess(outcome, escapeDc);
            var success = degree is Pf2eDegreeOfSuccess.Success or Pf2eDegreeOfSuccess.CriticalSuccess;

            if (success)
            {
                EngagementMutationHelper.ApplyGrappleEscape(action.CharacterId, targetId, mutations);
            }

            var resultSuffix = success ? " Actor breaks free." : string.Empty;
            return ResolverResult.Ok($"{action.ActionName}: {degree}. Rolled {outcome.Result} vs Escape DC {escapeDc}.{resultSuffix} {outcome.Summary}");
        }

        var actorSkill = action.Parameters.GetValueOrDefault("skill", "Athletics");
        var targetSkill = action.Parameters.GetValueOrDefault("targetSkill", actorSkill);

        var actorRollBonus = GetSkillOrAbilityBonus(actorStats, actorSkill);
        actorRollBonus = (await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, actorSkill, actorRollBonus, action).ConfigureAwait(false)).Bonus;

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (SocialSkillGating.ShouldApplyRelationshipModifier(System, action, actorSkill))
        {
            if (context.Characters.TryGetValue(action.CharacterId, out var actor))
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(
                    target, actor, CampaignConfigHelper.EffectiveConfig(context));
                actorRollBonus += relationshipBonus;
            }
        }

        var targetRollBonus = GetSkillOrAbilityBonus(targetStats, targetSkill);
        targetRollBonus = (await FoldAsync(context, target, RollKinds.Check, targetSkill, targetRollBonus, action).ConfigureAwait(false)).Bonus;

        var actorRoll = await _rollService.RollAsync(new RollRequest { Tag = "actor", Expression = "1d20", Bonus = actorRollBonus, Mechanic = GetMechanicFromAction(action) }, ct);
        var targetRoll = await _rollService.RollAsync(new RollRequest { Tag = "target", Expression = "1d20", Bonus = targetRollBonus, Mechanic = DiceMechanic.Standard }, ct);

        var actorWins = actorRoll.Result > targetRoll.Result;
        var resultStr = actorWins ? "Actor Wins" : "Target Wins";

        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";
        return ResolverResult.Ok($"{action.ActionName}: {resultStr}. Actor rolled {actorRoll.Result} ({actorSkill}){relationshipSuffix}, Target rolled {targetRoll.Result} ({targetSkill}).");
    }

    protected override async Task<ResolverResult> ResolveSavingThrowAsync(RulesetAction action, IChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        if (!TryResolveDc(action, actorStats, "Saving throw", out var dc, out var dcError))
        {
            return ResolverResult.Fail("InvalidParameter", dcError!);
        }

        var saveName = action.Parameters.GetValueOrDefault("save", "Constitution");
        var bonus = GetSavingThrowBonus(actorStats, saveName);
        var saveFold = await FoldAsync(
            context, context.Characters[action.CharacterId], RollKinds.Save, saveName, bonus, action, GetMechanicFromAction(action)).ConfigureAwait(false);
        bonus = saveFold.Bonus;

        var outcome = await _rollService.RollAsync(new RollRequest { Tag = "save", Expression = "1d20", Bonus = bonus, Mechanic = saveFold.Mechanic }, ct);

        var degree = CalculateDegreeOfSuccess(outcome, dc);

        var damage = await TryApplyPf2eSaveDamageAsync(action, action.CharacterId, degree, mutations, ct);
        var damageMsg = damage > 0 ? $" Took {damage} damage." : string.Empty;
        return ResolverResult.Ok($"{action.ActionName} ({saveName}): {degree}. Rolled {outcome.Result} vs DC {dc}.{damageMsg} {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveSpellSaveAsync(
        RulesetAction action,
        IChangeContext context,
        Pf2eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targets = AttackTargetHelper.SelectTargets(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: Spell save requires at least one target in targetIds.");
        }

        if (!TryResolveDc(action, actorStats, "Spell save", out var dc, out var dcError))
        {
            return ResolverResult.Fail("InvalidParameter", dcError!);
        }

        var saveName = action.Parameters.GetValueOrDefault("save", "Reflex");
        var narratives = new List<string>();

        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for spell save.");
            }

            if (target.SystemStats is not Pf2eExtension targetStats)
            {
                return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
            }

            var bonus = GetSavingThrowBonus(targetStats, saveName);
            bonus = (await FoldAsync(context, target, RollKinds.Save, saveName, bonus, action).ConfigureAwait(false)).Bonus;

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "spell-save",
                Expression = "1d20",
                Bonus = bonus,
                Mechanic = GetMechanicFromAction(action),
            }, ct);

            var degree = CalculateDegreeOfSuccess(outcome, dc);
            var damage = await TryApplyPf2eSaveDamageAsync(action, targetId, degree, mutations, ct);
            narratives.Add(
                $"{action.ActionName} vs {target.Name}: {degree} ({saveName} {outcome.Result} vs DC {dc})"
                + (damage > 0 ? $" — {damage} damage." : "."));
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    protected override async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        IChangeContext context,
        Pf2eExtension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.ContainsKey("dc"))
        {
            return ResolverResult.Ok(
                $"{action.ActionName}: Non-combat spell (detect aura, prestidigitation, message). Narrate effect; commit status or knowledge_update if the world changes.");
        }

        var skillName = action.Parameters.GetValueOrDefault("skill", "Arcana");
        action.Parameters["skill"] = skillName;
        return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
    }

    private async Task<int> TryApplyPf2eSaveDamageAsync(
        RulesetAction action,
        string targetId,
        Pf2eDegreeOfSuccess degree,
        List<WorldChange> mutations,
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

        var damageRoll = await _rollService.RollAsync(new RollRequest
        {
            Tag = "spell-damage",
            Expression = damageDice,
            Bonus = damageBonus,
            Mechanic = DiceMechanic.Standard,
        }, ct);

        var damage = degree switch
        {
            Pf2eDegreeOfSuccess.CriticalFailure => damageRoll.Result * 2,
            Pf2eDegreeOfSuccess.Failure => damageRoll.Result,
            Pf2eDegreeOfSuccess.Success => action.Parameters.TryGetValue("halfOnSave", out var halfStr)
                                             && (halfStr == "false" || halfStr == "0")
                ? damageRoll.Result
                : (int)Math.Floor(damageRoll.Result / 2.0),
            _ => 0,
        };

        if (damage > 0)
        {
            mutations.Add(new HpChange { CharacterId = targetId, Delta = -damage });
        }

        return damage;
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Pf2eExtension ?? new Pf2eExtension();
        var percKey = stats.SkillModifiers.Keys.FirstOrDefault(k => string.Equals(k, "Perception", StringComparison.OrdinalIgnoreCase));
        var initBonus = percKey != null && stats.SkillModifiers.TryGetValue(percKey, out var perc) ? perc : stats.WisdomMod;
        initBonus = Pipeline.Resolve(
            new RollQuery(RollKinds.Initiative, null, [], character, null, System, new Dictionary<string, string>()), initBonus).Bonus;
        
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = initBonus, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        
        // Use result + bonus as secondary tie-breaker
        // Widens initiative bonus scaling to meaningfully break ties
        return outcome.Result + (initBonus / 20f);
    }

    public override IReadOnlyDictionary<string, int> GetTurnActionBudget(Character character)
    {
        return new Dictionary<string, int> { { "actions", 3 } };
    }

    public override bool TryConsumeActionSlot(CombatantState state, RulesetAction action, out string? errorReason)
    {
        errorReason = null;

        if (action.IsReaction)
        {
            return true;
        }

        var cost = 1;
        if (action.Parameters.TryGetValue("actionCost", out var costStr) && !int.TryParse(costStr, out cost))
        {
            errorReason = $"Invalid actionCost value '{costStr}'.";
            return false;
        }
        cost = Math.Max(1, cost);

        if (!state.ActionBudget.TryGetValue("actions", out var remaining) || remaining < cost)
        {
            errorReason = $"Not enough actions remaining this turn (need {cost}, have {remaining}).";
            return false;
        }

        state.ActionBudget["actions"] -= cost;
        return true;
    }
}
