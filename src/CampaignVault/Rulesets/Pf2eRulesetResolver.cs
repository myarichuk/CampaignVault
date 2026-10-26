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
        RollModifierPipeline? rollModifiers = null,
        ClassDefinitionProvider? classProvider = null,
        BackgroundDefinitionProvider? backgroundProvider = null,
        ProgressionDefinitionProvider? progressionProvider = null)
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
        // Ancestry, class and background before HP and proficiency, which derive from what it sets.
        if (raceProvider != null || classProvider != null || backgroundProvider != null)
            steps.Add(new Pf2eDeriveOriginStep(raceProvider, classProvider, backgroundProvider, progressionProvider));
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

        // A skill with no recorded modifier is untrained: ability modifier only (no level, no rank).
        if (CampaignVault.Rulesets.Bootstrap.Pf2eSkillTable.KeyAbility.TryGetValue(name, out var keyAbility))
        {
            name = keyAbility;
        }
        else if (string.Equals(name, "Perception", StringComparison.OrdinalIgnoreCase))
        {
            name = "Wisdom";
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

        // No recorded save: a trained save (level + 2) on the key ability, since every PF2e character is at least
        // trained in all three. A raw ability name stays the bare modifier.
        var trained = stats.Level is > 0 ? stats.Level.Value + (int)Pf2eProficiencyRank.Trained : 0;
        return name.ToLower() switch
        {
            "fortitude" => stats.ConstitutionMod + trained,
            "reflex" => stats.DexterityMod + trained,
            "will" => stats.WisdomMod + trained,
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

        return ResolverResult.Ok(string.Join(" | ", narratives) + AttackTargetHelper.MultiTargetWarning(action, null));
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
        var actorChar = context.Characters[action.CharacterId];
        var acFold = await FoldAsync(context, target, RollKinds.ArmorClass, null, ac, action, other: actorChar).ConfigureAwait(false);
        ac = acFold.Bonus;
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
        {
            ac = overrideAc;
        }

        var attackBonus = 0;
        var hasExplicitBonus = TryGetParameter(action.Parameters, out var b, "bonus", "toHitBonus");
        if (hasExplicitBonus && !int.TryParse(b, out attackBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{b}'.");
        }

        // Ability modifier, level + proficiency and item bonuses come from the sheet unless the caller states the number.
        var isSpellAttack = action.ActionType == RulesetActionType.Spell;
        var derived = isSpellAttack
            ? DerivedAttack.Pf2eSpell(actorStats, action, hasExplicitBonus)
            : DerivedAttack.Pf2e(actorChar, actorStats, action, hasExplicitBonus, action.Parameters.ContainsKey("damageBonus") || DerivedAttack.HasInlineModifier(action.Parameters.GetValueOrDefault("damageDice")));
        attackBonus += derived.ToHit;
        var bareWarning = derived.Note + acFold.Suffix;

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
        else
        {
            // Strikes already made this turn (stamped by TryConsumeActionSlot) plus this action's own position in targetIds.
            var prior = action.Parameters.TryGetValue(PriorStrikesParameter, out var priorRaw) && int.TryParse(priorRaw, out var p) ? p : 0;
            // An agile weapon takes -4 and -8 instead of -5 and -10 (a spell attack carries no weapon, so no agile).
            var step = !isSpellAttack && WeaponProfile.Read(action).Agile ? 4 : 5;
            attackBonus -= Math.Min(prior + attackIndex, 2) * step;
        }

        var attackFold = await FoldAsync(
            context, context.Characters[action.CharacterId], RollKinds.Attack, null, attackBonus, action, GetMechanicFromAction(action), target).ConfigureAwait(false);
        attackBonus = attackFold.Bonus;
        bareWarning += attackFold.Suffix;

        var damageDice = action.Parameters.GetValueOrDefault("damageDice", "1d4");
        
        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageBonus value '{db}'.");
        }

        damageBonus += derived.Damage;

        var damageFold = await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Damage, null, damageBonus, action).ConfigureAwait(false);
        damageBonus = damageFold.Bonus;
        bareWarning += damageFold.Suffix;

        var attackRoll = await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = attackFold.Mechanic }, ct);
        
        var degree = CalculateDegreeOfSuccess(attackRoll, ac);

        if (degree == Pf2eDegreeOfSuccess.Failure || degree == Pf2eDegreeOfSuccess.CriticalFailure)
        {
            return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Missed. ({degree}) Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}{bareWarning}");
        }

        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);
        var finalDamage = damageRoll.Result;

        // Rogue precision damage: caller says the target is off-guard (sneakAttack: true); a critical doubles it with the rest.
        var sneakNote = "";
        var sneakDice = CombatFeatureRules.Pf2eSneakAttackDice(actorChar, actorStats.Level ?? 0);
        var calledOffGuard = action.Parameters.TryGetValue("sneakAttack", out var sneakRaw) && bool.TryParse(sneakRaw, out var sneakYes) && sneakYes;
        // The caller's flag stays the DM's override; a target that really is off-guard (prone, grabbed, paralyzed...) qualifies by itself.
        // Player Core: only agile, finesse, ranged or unarmed attacks deal sneak attack damage; an unknown weapon is given the benefit.
        var sneakWeapon = WeaponProfile.Read(action);
        var sneakEligible = !sneakWeapon.Known || sneakWeapon.Agile || sneakWeapon.Finesse || sneakWeapon.Ranged;
        if (!isSpellAttack && sneakDice > 0 && !sneakEligible && (calledOffGuard || Pipeline.HasCondition(target, System, "off-guard")))
        {
            sneakNote = " (no Sneak Attack: the weapon is not agile, finesse or ranged)";
        }
        if (!isSpellAttack && sneakDice > 0 && sneakEligible && (calledOffGuard || Pipeline.HasCondition(target, System, "off-guard")))
        {
            var sneakRoll = await _rollService.RollAsync(new RollRequest { Tag = "sneakAttack", Expression = $"{sneakDice}d6", Mechanic = DiceMechanic.Standard }, ct);
            finalDamage += sneakRoll.Result;
            sneakNote = $" Sneak Attack +{sneakRoll.Result} ({sneakDice}d6 precision).";
        }

        if (degree == Pf2eDegreeOfSuccess.CriticalSuccess)
        {
            finalDamage *= 2;
        }

        var targetEffects = action.FeatEffects.GetValueOrDefault(targetId) ?? [];
        var dealtNotes = new List<string>();
        finalDamage = DamageResolver.Resolve(finalDamage, action.DamageType ?? "Physical", targetStats, targetEffects, action, DamageOrdering.Pf2e, dealtNotes);
        if (dealtNotes.Count > 0)
        {
            bareWarning += $" ({string.Join("; ", dealtNotes)})";
        }

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage, Critical = degree == Pf2eDegreeOfSuccess.CriticalSuccess });

        return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Hit for {finalDamage} damage. ({degree}) Attack {attackRoll.Result} vs AC {ac}.{sneakNote}{bareWarning}");
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(RulesetAction action, IChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        if (!TryResolveDc(action, actorStats, "Skill check", out var dc, out var dcError))
        {
            return ResolverResult.Fail("InvalidParameter", dcError!);
        }

        var skillName = action.Parameters.GetValueOrDefault("skill", action.ActionName);
        var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        var bonusSource = "";
        if (action.Parameters.TryGetValue("bonus", out var suppliedBonus))
        {
            if (!int.TryParse(suppliedBonus, out var callerBonus))
            {
                return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{suppliedBonus}'.");
            }

            bonus = callerBonus;
            bonusSource = " [bonus supplied by caller; sheet modifier not added]";
        }

        var checkFold = await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, skillName, bonus, action, GetMechanicFromAction(action)).ConfigureAwait(false);
        bonus = checkFold.Bonus;

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

        var outcome = await _rollService.RollAsync(new RollRequest { Tag = "skill", Expression = "1d20", Bonus = bonus, Mechanic = checkFold.Mechanic }, ct);

        var degree = CalculateDegreeOfSuccess(outcome, dc);
        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";

        return new ResolverResult
        {
            Narrative = $"{action.ActionName} ({skillName}): {degree}. Rolled {outcome.Result} vs DC {dc}.{relationshipSuffix} {outcome.Summary}{checkFold.Suffix}{bonusSource}",
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

        var isEscape = EngagementMutationHelper.IsEscapeGrappleAction(action);
        var isGrapple = !isEscape && EngagementMutationHelper.IsGrappleAction(action);

        // Grapple and Escape have the attack trait: they take and count toward the multiple attack penalty.
        var mapPenalty = MultipleAttackPenalty(action);
        var mapNote = mapPenalty < 0 ? $" MAP {mapPenalty}." : "";

        if (isGrapple)
        {
            var skillName = action.Parameters.GetValueOrDefault("skill", "Athletics");
            var bonus = GetSkillOrAbilityBonus(actorStats, skillName) + mapPenalty;
            var grabFold = await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, skillName, bonus, action, GetMechanicFromAction(action)).ConfigureAwait(false);
            bonus = grabFold.Bonus;

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
                Mechanic = grabFold.Mechanic
            }, ct);

            var degree = CalculateDegreeOfSuccess(outcome, fortDc);
            var success = degree is Pf2eDegreeOfSuccess.Success or Pf2eDegreeOfSuccess.CriticalSuccess;

            // Player Core: a critical success restrains, a success grabs, a critical failure drops any hold the actor already
            // had and lets the target grab them or force them prone.
            string resultSuffix;
            switch (degree)
            {
                case Pf2eDegreeOfSuccess.CriticalSuccess:
                    EngagementMutationHelper.ApplyGrappleSuccess(action.CharacterId, targetId, mutations, EngagementMutationHelper.RestrainedCondition);
                    resultSuffix = " Target is now restrained (off-guard, can't act except to Escape).";
                    break;
                case Pf2eDegreeOfSuccess.Success:
                    EngagementMutationHelper.ApplyGrappleSuccess(action.CharacterId, targetId, mutations, EngagementMutationHelper.GrabbedCondition);
                    resultSuffix = " Target is now grabbed.";
                    break;
                case Pf2eDegreeOfSuccess.CriticalFailure:
                    EngagementMutationHelper.ApplyGrappleEscape(targetId, action.CharacterId, mutations, EngagementMutationHelper.GrabbedCondition);
                    resultSuffix = $" Critical failure: any hold on {target.Name} is lost, and {target.Name} may grab {context.Characters[action.CharacterId].Name} or force them prone (apply the status by hand).";
                    break;
                default:
                    resultSuffix = string.Empty;
                    break;
            }

            return ResolverResult.Ok($"{action.ActionName}: {degree}. Rolled {outcome.Result} vs Fortitude DC {fortDc}.{resultSuffix}{mapNote} {outcome.Summary}{grabFold.Suffix}");
        }

        if (isEscape)
        {
            // The escaper picks their best of unarmed attack, Acrobatics or Athletics; the DC is always the grappler's Athletics DC.
            var skillName = action.Parameters.GetValueOrDefault("skill", "Athletics");
            var actorBonus = GetSkillOrAbilityBonus(actorStats, skillName) + mapPenalty;
            var escapeFold = await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, skillName, actorBonus, action, GetMechanicFromAction(action)).ConfigureAwait(false);
            actorBonus = escapeFold.Bonus;

            var grapplerBonus = GetSkillOrAbilityBonus(targetStats, "Athletics");
            grapplerBonus = (await FoldAsync(context, target, RollKinds.Check, "Athletics", grapplerBonus, action).ConfigureAwait(false)).Bonus;
            var escapeDc = 10 + grapplerBonus;

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "escape",
                Expression = "1d20",
                Bonus = actorBonus,
                Mechanic = escapeFold.Mechanic
            }, ct);

            var degree = CalculateDegreeOfSuccess(outcome, escapeDc);
            var success = degree is Pf2eDegreeOfSuccess.Success or Pf2eDegreeOfSuccess.CriticalSuccess;

            if (success)
            {
                EngagementMutationHelper.ApplyGrappleEscape(action.CharacterId, targetId, mutations, EngagementMutationHelper.GrabbedCondition);
            }

            var resultSuffix = success ? " Actor breaks free." : string.Empty;
            return ResolverResult.Ok($"{action.ActionName}: {degree}. Rolled {outcome.Result} vs Escape DC {escapeDc} (grappler's Athletics).{resultSuffix}{mapNote} {outcome.Summary}{escapeFold.Suffix}");
        }

        var actorSkill = action.Parameters.GetValueOrDefault("skill", "Athletics");
        var targetSkill = action.Parameters.GetValueOrDefault("targetSkill", actorSkill);

        var actorRollBonus = GetSkillOrAbilityBonus(actorStats, actorSkill);
        var actorFold = await FoldAsync(context, context.Characters[action.CharacterId], RollKinds.Check, actorSkill, actorRollBonus, action, GetMechanicFromAction(action), target).ConfigureAwait(false);
        actorRollBonus = actorFold.Bonus;

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
        var targetFold = await FoldAsync(context, target, RollKinds.Check, targetSkill, targetRollBonus, other: context.Characters[action.CharacterId]).ConfigureAwait(false);
        targetRollBonus = targetFold.Bonus;

        var actorRoll = await _rollService.RollAsync(new RollRequest { Tag = "actor", Expression = "1d20", Bonus = actorRollBonus, Mechanic = actorFold.Mechanic }, ct);
        var targetRoll = await _rollService.RollAsync(new RollRequest { Tag = "target", Expression = "1d20", Bonus = targetRollBonus, Mechanic = targetFold.Mechanic }, ct);

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

        if (TryResolveSaveName(action, LookupSpell(action), "Saving throw", out var saveName, out var saveSource) is { } saveNameError)
        {
            return saveNameError;
        }

        var bonus = GetSavingThrowBonus(actorStats, saveName);
        var saveFold = await FoldAsync(
            context, context.Characters[action.CharacterId], RollKinds.Save, saveName, bonus, action, GetMechanicFromAction(action)).ConfigureAwait(false);
        bonus = saveFold.Bonus;

        var outcome = await _rollService.RollAsync(new RollRequest { Tag = "save", Expression = "1d20", Bonus = bonus, Mechanic = saveFold.Mechanic }, ct);

        var degree = saveFold.AutoFail ? Pf2eDegreeOfSuccess.CriticalFailure : CalculateDegreeOfSuccess(outcome, dc);

        var damage = await TryApplyPf2eSaveDamageAsync(action, action.CharacterId, actorStats, degree, new SaveDamageRoll(), mutations, ct);
        var damageMsg = damage > 0 ? $" Took {damage} damage." : string.Empty;
        return ResolverResult.Ok($"{action.ActionName} ({saveName}): {degree}. Rolled {outcome.Result} vs DC {dc}.{damageMsg} {outcome.Summary}{saveFold.Suffix}{saveSource}");
    }

    /// <summary>The handbook entry named by the action, when the provider knows it (for saveType and summon data).</summary>
    private SpellDefinition? LookupSpell(RulesetAction action) =>
        _spellDefinitionProvider is not null
        && _spellDefinitionProvider.TryGet(System, NormalizeSpellSlug(action.ActionName), out var spell)
            ? spell
            : null;

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

        if (TryResolveSaveName(action, LookupSpell(action), "Spell save", out var saveName, out var saveSource) is { } saveNameError)
        {
            return saveNameError;
        }

        var narratives = new List<string>();
        var rolledDamage = new SaveDamageRoll();

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
            var spellSaveFold = await FoldAsync(context, target, RollKinds.Save, saveName, bonus, action, GetMechanicFromAction(action)).ConfigureAwait(false);
            bonus = spellSaveFold.Bonus;

            var outcome = await _rollService.RollAsync(new RollRequest
            {
                Tag = "spell-save",
                Expression = "1d20",
                Bonus = bonus,
                Mechanic = spellSaveFold.Mechanic,
            }, ct);

            var degree = spellSaveFold.AutoFail ? Pf2eDegreeOfSuccess.CriticalFailure : CalculateDegreeOfSuccess(outcome, dc);
            var damage = await TryApplyPf2eSaveDamageAsync(action, targetId, targetStats, degree, rolledDamage, mutations, ct);
            narratives.Add(
                $"{action.ActionName} vs {target.Name}: {degree} ({saveName} {outcome.Result} vs DC {dc})"
                + (damage > 0 ? $" — {damage} damage." : "."));
        }

        return ResolverResult.Ok(string.Join(" | ", narratives) + saveSource);
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
        Pf2eExtension targetStats,
        Pf2eDegreeOfSuccess degree,
        SaveDamageRoll rolled,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!rolled.IsRolled)
        {
            rolled.IsRolled = true;
            if (action.Parameters.TryGetValue("damageDice", out var damageDice))
            {
                var damageBonus = 0;
                if (action.Parameters.TryGetValue("damageBonus", out var db))
                {
                    DamageResolver.TryParseBonus(db, out damageBonus);
                }

                // One roll per cast: each target scales the same number by its own degree of success.
                var damageRoll = await _rollService.RollAsync(new RollRequest
                {
                    Tag = "spell-damage",
                    Expression = damageDice,
                    Bonus = damageBonus,
                    Mechanic = DiceMechanic.Standard,
                }, ct);
                rolled.Packets = [new DamagePacket(damageRoll.Result, action.DamageType, Primary: true)];
            }
        }

        if (rolled.Packets.Count == 0)
        {
            return 0;
        }

        var halfOnSave = !(action.Parameters.TryGetValue("halfOnSave", out var halfStr) && (halfStr == "false" || halfStr == "0"));
        var scaled = rolled.Packets
            .Select(p => p with
            {
                Amount = degree switch
                {
                    Pf2eDegreeOfSuccess.CriticalFailure => p.Amount * 2,
                    Pf2eDegreeOfSuccess.Failure => p.Amount,
                    Pf2eDegreeOfSuccess.Success => halfOnSave ? (int)Math.Floor(p.Amount / 2.0) : p.Amount,
                    _ => 0,
                },
            })
            .ToList();

        var targetEffects = action.FeatEffects.GetValueOrDefault(targetId) ?? [];
        var damage = DamageResolver.Resolve(scaled, targetStats, targetEffects, action, DamageOrdering.Pf2e).Total;
        if (damage > 0)
        {
            mutations.Add(new HpChange { CharacterId = targetId, Delta = -damage, Critical = degree == Pf2eDegreeOfSuccess.CriticalFailure });
        }

        return damage;
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Pf2eExtension ?? new Pf2eExtension();
        var percKey = stats.SkillModifiers.Keys.FirstOrDefault(k => string.Equals(k, "Perception", StringComparison.OrdinalIgnoreCase));
        var initBonus = percKey != null && stats.SkillModifiers.TryGetValue(percKey, out var perc) ? perc : stats.WisdomMod;
        var initiative = Pipeline.Resolve(
            new RollQuery(RollKinds.Initiative, null, [], character, null, System, new Dictionary<string, string>()), initBonus);
        initBonus = initiative.Bonus;
        
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = initBonus, Mechanic = initiative.Advantage switch
        {
            AdvantageEffect.Advantage => DiceMechanic.Advantage,
            AdvantageEffect.Disadvantage => DiceMechanic.Disadvantage,
            _ => DiceMechanic.Standard,
        } };
        var outcome = await _rollService.RollAsync(request, ct);
        
        // Use result + bonus as secondary tie-breaker
        // Widens initiative bonus scaling to meaningfully break ties
        return outcome.Result + (initBonus / 20f);
    }

    public override IReadOnlyDictionary<string, int> GetTurnActionBudget(Character character)
    {
        return new Dictionary<string, int> { { "actions", 3 } };
    }

    /// <summary>The multiple attack penalty an attack-trait action other than a Strike takes: -5 per prior attack this turn, to -10.</summary>
    private static int MultipleAttackPenalty(RulesetAction action)
    {
        if (action.Parameters.TryGetValue("mapPenalty", out var mapStr) && int.TryParse(mapStr, out var mapVal))
            return -Math.Abs(mapVal);
        var prior = action.Parameters.TryGetValue(PriorStrikesParameter, out var priorRaw) && int.TryParse(priorRaw, out var p) ? p : 0;
        return -Math.Min(prior, 2) * 5;
    }

    public const string RaisedShieldEffectKey = "pf2e:raised-shield";

    /// <summary>
    /// Raise a Shield (one action): the equipped shield's AC bonus applies until the start of the actor's next turn, as a
    /// status effect the pipeline folds into AC. The shield gives nothing while lowered (ArmorParameterResolver leaves it
    /// out of the static AC for pf2e).
    /// </summary>
    protected override async Task<ResolverResult?> TryResolveClassFeatureAsync(
        RulesetAction action, IChangeContext context, Pf2eExtension stats, List<WorldChange> mutations, CancellationToken ct)
    {
        if (CombatFeatureRules.Norm(action.ActionName) is not ("raiseashield" or "raiseshield"))
            return null;

        var actor = context.Characters.GetValueOrDefault(action.CharacterId);
        if (actor is null)
            return ResolverResult.Fail("InvalidActor", "Error: the actor was not found.");
        if (stats.StatusEffects.Any(e => e.EffectKey == RaisedShieldEffectKey))
            return ResolverResult.Ok($"{actor.Name}'s shield is already raised until their next turn.");

        var equipped = await ItemHolderQueryHelper.GetEquippedItemsAsync(context, action.CharacterId, ct: ct).ConfigureAwait(false);
        var shield = equipped.FirstOrDefault(i =>
            i.Tags.Contains("shield", StringComparer.OrdinalIgnoreCase)
            || (i.EquipLayer == EquipLayers.Held && i.EquipZones.Contains(EquipZones.OffHand, StringComparer.OrdinalIgnoreCase)));
        if (shield is null)
            return ResolverResult.Fail("NoShield", $"Error: {actor.Name} has no shield equipped to raise.");

        var acBonus = shield.Properties.TryGetValue("acBonus", out var raw) && int.TryParse(raw?.ToString(), out var parsed) ? parsed : 2;
        mutations.Add(new StatusChange
        {
            CharacterId = action.CharacterId,
            Effect = new StatusEffect
            {
                Name = "Raised Shield",
                Category = "Buff",
                EffectKey = RaisedShieldEffectKey,
                StatModifiers = new Dictionary<string, float> { ["AC"] = acBonus },
                ExpiresAtOwnTurnStart = true,
                AppliedBy = "system/combat-resolver",
            },
        });
        return ResolverResult.Ok($"{actor.Name} raises {shield.Name}: +{acBonus} AC until the start of their next turn.");
    }

    private const string StrikesMade = "strikesMade";
    private const string PriorStrikesParameter = "priorStrikes";

    public override bool TryConsumeActionSlot(CombatantState state, RulesetAction action, out string? errorReason)
    {
        errorReason = null;
        action.Parameters.Remove(PriorStrikesParameter);

        if (action.IsReaction)
        {
            return true;
        }

        var cost = 1;
        if (action.Parameters.TryGetValue("actionCost", out var costStr) && !int.TryParse(costStr, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out cost))
        {
            errorReason = $"Invalid actionCost value '{costStr}'.";
            return false;
        }
        if (cost < 0)
        {
            errorReason = $"Invalid actionCost value '{costStr}': it can't be negative.";
            return false;
        }

        if (!state.ActionBudget.TryGetValue("actions", out var remaining) || remaining < cost)
        {
            errorReason = $"Not enough actions remaining this turn (need {cost}, have {remaining}).";
            return false;
        }

        state.ActionBudget["actions"] -= cost;

        // Multiple attack penalty across separate actions: remember Strikes this turn; the resolver reads the count back.
        // A spell that rolls an attack counts too; a save spell does not.
        if (action.ActionType == RulesetActionType.Attack
            || (action.ActionType == RulesetActionType.Spell && SpellResolutionHelper.InferMode(action) == SpellResolutionMode.Attack)
            || EngagementMutationHelper.IsGrappleAction(action) || EngagementMutationHelper.IsEscapeGrappleAction(action))
        {
            var made = state.ActionBudget.GetValueOrDefault(StrikesMade);
            action.Parameters[PriorStrikesParameter] = made.ToString();
            state.ActionBudget[StrikesMade] = made + Math.Max(1, AttackTargetHelper.SelectAttackInstances(action).Count);
        }

        return true;
    }
}
