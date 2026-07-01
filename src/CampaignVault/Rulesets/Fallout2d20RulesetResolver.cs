using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Rulesets;

public class Fallout2d20RulesetResolver : RulesetResolverBase<Fallout2d20Extension>
{
    private readonly IRollService _rollService;
    private readonly ICharacterBootstrapPipeline _bootstrap;

    public Fallout2d20RulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
        var hpStep = new FalloutDeriveHitPointsStep();
        _bootstrap = new CharacterBootstrapPipeline(
            [hpStep, new FalloutDeriveDefenseStep()],
            [hpStep]);
    }

    public override RulesetSystem System => RulesetSystem.Fallout2d20;

    public override ICharacterBootstrapPipeline Bootstrap => _bootstrap;

    protected override IRollService? GetRollService() => _rollService;

    private static bool ShouldApplyRelationshipModifier(RulesetAction action, string skillName)
    {
        if (action.ActionCategory == ActionCategory.Social)
        {
            return true;
        }

        var socialSkills = new[] { "Persuasion", "Deception", "Intimidation", "Insight", "Performance" };
        return socialSkills.Any(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid difficulty value '{diffStr}'.");
        }

        var attribute = action.Parameters.GetValueOrDefault("attribute", "Agility");
        var skill = action.Parameters.GetValueOrDefault("skill", "SmallGuns");

        var request = FalloutPoolHelper.BuildPoolRequest(
            actorStats, attribute, skill, action.Parameters, "skill", ApplyAllModifiers, "SkillCheck", skill, attribute);

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (ShouldApplyRelationshipModifier(action, skill))
        {
            var targetId = action.TargetIds.FirstOrDefault();
            if (targetId != null && context.Characters.TryGetValue(targetId, out var target) &&
                context.Characters.TryGetValue(action.CharacterId, out var actor) && context.Config != null)
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(target, actor, context.Config);
                request.TargetNumber += relationshipBonus;
            }
        }

        var outcome = await _rollService.RollAsync(request, ct);

        var success = outcome.Successes >= difficulty;
        var apGenerated = Math.Max(0, outcome.Successes - difficulty);
        var compMsg = outcome.HasComplication ? " COMPLICATION ROLLED!" : "";
        var relationshipSuffix = relationshipBonus != 0 ? $" ({relationshipLabel})" : "";

        return ResolverResult.Ok(
            $"{action.ActionName} ({attribute}+{skill} TN {request.TargetNumber}){relationshipSuffix}: {(success ? "Success" : "Failure")}. Generated {apGenerated} AP.{compMsg} {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveAttackAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
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
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for attack.");
        }

        if (target.SystemStats is not Fallout2d20Extension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var attribute = action.Parameters.GetValueOrDefault("attribute", "Agility");
        var skill = action.Parameters.GetValueOrDefault("skill", "SmallGuns");
        var difficulty = FalloutPoolHelper.ResolveAttackDifficulty(targetStats, action.Parameters, ApplyAllModifiers);

        var request = FalloutPoolHelper.BuildPoolRequest(
            actorStats, attribute, skill, action.Parameters, "attack", ApplyAllModifiers, "AttackRoll", skill, attribute);

        var outcome = await _rollService.RollAsync(request, ct);
        var success = outcome.Successes >= difficulty;
        var compMsg = outcome.HasComplication ? " COMPLICATION!" : "";

        if (!success)
        {
            return ResolverResult.Ok($"{action.ActionName} vs {target.Name}: Missed (need {difficulty} successes).{compMsg} {outcome.Summary}");
        }

        var combatDiceCount = 3;
        if (action.Parameters.TryGetValue("damageDice", out var cd) && !int.TryParse(cd, out combatDiceCount))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageDice value '{cd}'.");
        }

        combatDiceCount = ApplyAllModifiers(actorStats, combatDiceCount, "DamageRoll");
        var damageType = action.DamageType ?? (action.Parameters.GetValueOrDefault("damageType", "Physical"));
        var targetPart = action.Parameters.GetValueOrDefault("targetPart");

        var combatResult = await _rollService.RollFalloutCombatDiceAsync(combatDiceCount, ct);

        var isVicious = action.Parameters.TryGetValue("vicious", out var vicStr) &&
                        (vicStr == "true" || vicStr == "1" || (bool.TryParse(vicStr, out var vb) && vb));

        var piercingRating = 0;
        if (action.Parameters.TryGetValue("piercing", out var pierceStr))
        {
            int.TryParse(pierceStr, out piercingRating);
        }

        var damageBonusFromEffects = isVicious ? combatResult.Effects : 0;
        var ignoredDr = combatResult.Effects * piercingRating;

        var drKey = targetStats.DamageResistance.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        var dr = drKey != null && targetStats.DamageResistance.TryGetValue(drKey, out var res) ? res : 0;

        var effectiveDr = Math.Max(0, dr - ignoredDr);
        var totalDamage = combatResult.Damage + damageBonusFromEffects;
        var finalDamage = Math.Max(0, totalDamage - effectiveDr);

        var modKey = targetStats.DamageModifiers.Keys.FirstOrDefault(k => string.Equals(k, damageType, StringComparison.OrdinalIgnoreCase));
        if (modKey != null && targetStats.DamageModifiers.TryGetValue(modKey, out var multiplier))
        {
            finalDamage = (int)Math.Floor(finalDamage * multiplier);
        }

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        var locationMsg = targetPart is not null ? $" Location: {targetPart}." : string.Empty;
        return ResolverResult.Ok(
            $"{action.ActionName} vs {target.Name}: Hit for {finalDamage} damage ({combatResult.Effects} Effects).{locationMsg}{compMsg}");
    }

    protected override async Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for contested check.");
        }

        if (target.SystemStats is not Fallout2d20Extension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var isGrapple = EngagementMutationHelper.IsGrappleAction(action);
        var isEscape = EngagementMutationHelper.IsEscapeGrappleAction(action);

        var actorAttribute = action.Parameters.TryGetValue("attribute", out var actorAttr)
            ? actorAttr
            : isGrapple || isEscape ? "Strength" : "Agility";
        var actorSkill = action.Parameters.GetValueOrDefault("skill", "Athletics");

        var targetAttribute = action.Parameters.GetValueOrDefault("targetAttribute", actorAttribute);
        var targetSkill = action.Parameters.GetValueOrDefault("targetSkill", actorSkill);

        var actorRequest = FalloutPoolHelper.BuildPoolRequest(
            actorStats, actorAttribute, actorSkill, action.Parameters, "actor", ApplyAllModifiers, "SkillCheck", actorSkill, actorAttribute);

        var relationshipLabel = "neutral";
        var relationshipBonus = 0;
        if (!isGrapple && !isEscape && ShouldApplyRelationshipModifier(action, actorSkill))
        {
            if (context.Characters.TryGetValue(action.CharacterId, out var actor) && context.Config != null)
            {
                (relationshipBonus, relationshipLabel) = RelationshipModifierHelper.GetSocialModifier(target, actor, context.Config);
                actorRequest.TargetNumber += relationshipBonus;
            }
        }

        var targetRequest = FalloutPoolHelper.BuildPoolRequest(
            targetStats, targetAttribute, targetSkill, action.Parameters, "target", ApplyAllModifiers, "SkillCheck", targetSkill, targetAttribute);

        var actorOutcome = await _rollService.RollAsync(actorRequest, ct);
        var targetOutcome = await _rollService.RollAsync(targetRequest, ct);

        var actorWins = actorOutcome.Successes > targetOutcome.Successes;
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
        return ResolverResult.Ok(
            $"{action.ActionName}: {resultStr}. Actor {actorOutcome.Successes} successes ({actorAttribute}+{actorSkill}){relationshipSuffix}, Target {targetOutcome.Successes} successes ({targetAttribute}+{targetSkill}). {actorOutcome.Summary} vs {targetOutcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveSavingThrowAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && !int.TryParse(diffStr, out difficulty))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid difficulty value '{diffStr}'.");
        }

        if (action.Parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var dc))
        {
            difficulty = dc;
        }

        var attribute = action.Parameters.GetValueOrDefault("attribute", "Endurance");
        var skill = action.Parameters.GetValueOrDefault("skill");

        var request = skill is not null
            ? FalloutPoolHelper.BuildPoolRequest(actorStats, attribute, skill, action.Parameters, "save", ApplyAllModifiers, "SavingThrow", skill, attribute)
            : BuildAttributeOnlyPool(actorStats, attribute, action.Parameters, "save");

        var outcome = await _rollService.RollAsync(request, ct);

        var success = outcome.Successes >= difficulty;
        var apGenerated = Math.Max(0, outcome.Successes - difficulty);
        var compMsg = outcome.HasComplication ? " COMPLICATION ROLLED!" : "";

        var damage = 0;
        if (!success && action.Parameters.TryGetValue("damageDice", out var damageDiceStr) && int.TryParse(damageDiceStr, out var diceCount))
        {
            var combatResult = await _rollService.RollFalloutCombatDiceAsync(diceCount, ct);
            damage = combatResult.Damage;
            if (damage > 0)
            {
                mutations.Add(new HpChange { CharacterId = action.CharacterId, Delta = -damage });
            }
        }

        var damageMsg = damage > 0 ? $" Took {damage} damage." : string.Empty;
        return ResolverResult.Ok(
            $"{action.ActionName} ({attribute}{(skill != null ? "+" + skill : "")} TN {request.TargetNumber}): {(success ? "Success" : "Failure")}. Generated {apGenerated} AP.{damageMsg}{compMsg} {outcome.Summary}");
    }

    protected override async Task<ResolverResult> ResolveSpellSaveAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targets = AttackTargetHelper.SelectTargets(action);
        if (targets.Count == 0)
        {
            return ResolverResult.Fail("InvalidTarget", "Error: Explosive/radiation effect requires targetIds.");
        }

        var difficulty = 1;
        if (action.Parameters.TryGetValue("difficulty", out var diffStr) && int.TryParse(diffStr, out var explicitDifficulty))
        {
            difficulty = explicitDifficulty;
        }
        else if (action.Parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var dc))
        {
            difficulty = dc;
        }

        var attribute = action.Parameters.TryGetValue("saveAttribute", out var saveAttr)
            ? saveAttr
            : action.Parameters.GetValueOrDefault("attribute", "Endurance");
        var skill = action.Parameters.TryGetValue("saveSkill", out var saveSkill)
            ? saveSkill
            : action.Parameters.GetValueOrDefault("skill");

        var damageDice = 3;
        if (action.Parameters.TryGetValue("damageDice", out var dd) && int.TryParse(dd, out var parsedDice))
        {
            damageDice = parsedDice;
        }

        var narratives = new List<string>();
        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found.");
            }

            if (target.SystemStats is not Fallout2d20Extension targetStats)
            {
                return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats.");
            }

            var request = skill is not null
                ? FalloutPoolHelper.BuildPoolRequest(targetStats, attribute, skill, action.Parameters, "spell-save", ApplyAllModifiers, "SavingThrow", skill, attribute)
                : BuildAttributeOnlyPool(targetStats, attribute, action.Parameters, "spell-save");

            var outcome = await _rollService.RollAsync(request, ct);
            var success = outcome.Successes >= difficulty;
            var damage = 0;

            if (!success)
            {
                var combatResult = await _rollService.RollFalloutCombatDiceAsync(damageDice, ct);
                damage = combatResult.Damage;
                if (damage > 0)
                {
                    mutations.Add(new HpChange { CharacterId = targetId, Delta = -damage });
                }
            }

            narratives.Add(
                $"{action.ActionName} vs {target.Name}: {(success ? "Resisted" : "Failed")} ({outcome.Successes} vs difficulty {difficulty})"
                + (damage > 0 ? $" — {damage} damage." : "."));
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    protected override async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (!action.Parameters.ContainsKey("difficulty") && !action.Parameters.ContainsKey("dc"))
        {
            return ResolverResult.Ok(
                $"{action.ActionName}: Weird science / exploration action outside combat. Narrate fabrication, hacking, or chem effects; commit item or status changes separately.");
        }

        var attribute = action.Parameters.GetValueOrDefault("attribute", "Intelligence");
        var skill = action.Parameters.GetValueOrDefault("skill", "Science");
        action.Parameters.TryAdd("attribute", attribute);
        action.Parameters.TryAdd("skill", skill);

        if (action.Parameters.TryGetValue("dc", out var dcStr) && int.TryParse(dcStr, out var dc))
        {
            action.Parameters["difficulty"] = dc.ToString();
        }

        return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
    }

    protected override async Task<ResolverResult> ResolveRecoveryAsync(
        RulesetAction action,
        ChangeContext context,
        Fallout2d20Extension actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targets = action.TargetIds.Count > 0 ? action.TargetIds : [action.CharacterId];
        var healAmount = 0;

        if (action.Parameters.TryGetValue("healAmount", out var healStr) && int.TryParse(healStr, out var flatHeal))
        {
            healAmount = flatHeal;
        }
        else if (action.Parameters.TryGetValue("healDice", out var healDiceStr) && int.TryParse(healDiceStr, out var diceCount))
        {
            var combatResult = await _rollService.RollFalloutCombatDiceAsync(diceCount, ct);
            healAmount = Math.Max(1, combatResult.Damage);
        }
        else
        {
            healAmount = 4;
        }

        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for recovery.");
            }

            mutations.Add(new HpChange { CharacterId = targetId, Delta = healAmount });
        }

        return ResolverResult.Ok($"{action.ActionName}: Restored {healAmount} HP to {targets.Count} target(s).");
    }

    private RollRequest BuildAttributeOnlyPool(
        Fallout2d20Extension stats,
        string attribute,
        IReadOnlyDictionary<string, string> parameters,
        string tag)
    {
        var poolSize = FalloutPoolHelper.ResolvePoolSize(stats, parameters, tag == "target" ? "targetPool" : "pool");
        var targetNumber = ApplyAllModifiers(stats, GetAttributeValue(stats, attribute), "SavingThrow", attribute);
        return new RollRequest
        {
            Tag = tag,
            Expression = $"{poolSize}d20",
            Mechanic = DiceMechanic.SuccessCount,
            TargetNumber = targetNumber,
        };
    }

    private static int GetAttributeValue(Fallout2d20Extension stats, string name) =>
        name.ToLowerInvariant() switch
        {
            "strength" => stats.Strength,
            "perception" => stats.Perception,
            "endurance" => stats.Endurance,
            "charisma" => stats.Charisma,
            "intelligence" => stats.Intelligence,
            "agility" => stats.Agility,
            "luck" => stats.Luck,
            _ => 5,
        };

    public override Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Fallout2d20Extension ?? new Fallout2d20Extension();
        var initiative = stats.Perception + stats.Agility;
        initiative = ApplyAllModifiers(stats, initiative, "Initiative");

        return Task.FromResult((float)initiative);
    }
}