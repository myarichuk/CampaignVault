using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

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

    public Pf2eRulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
    }

    public override RulesetSystem System => RulesetSystem.Pathfinder2e;

    private int GetSkillOrAbilityBonus(Pf2eExtension stats, string name)
    {
        if (stats.SkillModifiers.TryGetValue(name, out var skillMod))
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

    protected override async Task<ResolverResult> ResolveAttackAsync(RulesetAction action, ChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
        {
            return ResolverResult.Fail("InvalidTarget", "Error: No valid target specified for attack.");
        }

        if (target.SystemStats is not Pf2eExtension targetStats)
        {
            return ResolverResult.Fail("IncompatibleRuleset", "Error: Target uses incompatible ruleset stats for current ActiveSystem.");
        }

        var ac = targetStats.ArmorClass;
        ac = ApplyAllModifiers(targetStats, "AC", ac);
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
        {
            ac = overrideAc;
        }

        var attackBonus = 0;
        if (action.Parameters.TryGetValue("bonus", out var b) && !int.TryParse(b, out attackBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid bonus value '{b}'.");
        }

        attackBonus = ApplyAllModifiers(actorStats, "AttackRoll", attackBonus);

        var damageDice = action.Parameters.TryGetValue("damageDice", out var dd) ? dd : "1d4";
        
        var damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid damageBonus value '{db}'.");
        }

        damageBonus = ApplyAllModifiers(actorStats, "DamageRoll", damageBonus);

        var attackRoll = await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = DiceMechanic.Standard }, ct);
        
        var degree = CalculateDegreeOfSuccess(attackRoll, ac);

        if (degree == Pf2eDegreeOfSuccess.Failure || degree == Pf2eDegreeOfSuccess.CriticalFailure)
        {
            return ResolverResult.Ok($"{action.ActionName}: Missed. ({degree}) Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}");
        }

        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);
        var finalDamage = damageRoll.Result;

        if (degree == Pf2eDegreeOfSuccess.CriticalSuccess)
        {
            // Per Pathfinder 2e CRB: "When you critically succeed at a Strike, you double the damage you deal. ... roll the damage normally, including any modifiers, bonuses, and penalties, and then you double the entire amount."
            finalDamage *= 2;
        }

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        return ResolverResult.Ok($"{action.ActionName}: Hit for {finalDamage} damage. ({degree}) Attack {attackRoll.Result} vs AC {ac}.");
    }

    protected override async Task<ResolverResult> ResolveSkillCheckAsync(RulesetAction action, ChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
        {
            return ResolverResult.Fail("InvalidParameter", "Error: Skill check requires a 'dc' parameter.");
        }

        var skillName = action.Parameters.TryGetValue("skill", out var s) ? s : "Strength";
        var bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        bonus = ApplyAllModifiers(actorStats, "SkillCheck", bonus);
        bonus = ApplyAllModifiers(actorStats, skillName, bonus);

        var outcome = await _rollService.RollAsync(new RollRequest { Tag = "skill", Expression = "1d20", Bonus = bonus, Mechanic = DiceMechanic.Standard }, ct);
        
        var degree = CalculateDegreeOfSuccess(outcome, dc);
        
        return ResolverResult.Ok($"{action.ActionName} ({skillName}): {degree}. Rolled {outcome.Result} vs DC {dc}. {outcome.Summary}");
    }

    protected override Task<ResolverResult> ResolveContestedCheckAsync(RulesetAction action, ChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        return Task.FromResult(ResolverResult.Fail("NotImplemented", "PF2e: Contested checks are typically resolved against DCs instead of opposed rolls."));
    }

    protected override Task<ResolverResult> ResolveSavingThrowAsync(RulesetAction action, ChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        return Task.FromResult(ResolverResult.Fail("NotImplemented", "PF2e: Saving throws are typically resolved against DCs. Needs implementation."));
    }

    public override async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var stats = character.SystemStats as Pf2eExtension ?? new Pf2eExtension();
        var initBonus = stats.SkillModifiers.TryGetValue("Perception", out var perc) ? perc : stats.WisdomMod;
        initBonus = ApplyAllModifiers(stats, "Initiative", initBonus);
        
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = initBonus, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        
        // Use result + bonus as secondary tie-breaker
        return outcome.Result + (initBonus * 0.01f);
    }
}
