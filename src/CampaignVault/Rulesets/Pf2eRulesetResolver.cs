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

public class Pf2eRulesetResolver : IRulesetResolver
{
    private readonly IRollService _rollService;

    public Pf2eRulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
    }

    public RulesetSystem System => RulesetSystem.Pathfinder2e;

    public async Task<ResolverOutput> ResolveAsync(ChangeContext context, RulesetAction action, CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.ActorId, out var actor))
            return new ResolverOutput { Result = new ResolverResult { Narrative = $"Error: Actor '{action.ActorId}' not found." } };

        if (actor.SystemStats is not Pf2eExtension actorStats)
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
                narrative = $"PF2e: Action type {action.ActionType} not yet fully implemented.";
                break;
        }

        return new ResolverOutput { Mutations = mutations, Result = new ResolverResult { Narrative = narrative } };
    }

    private int GetSkillOrAbilityBonus(Pf2eExtension stats, string name)
    {
        if (stats.SkillModifiers.TryGetValue(name, out var skillMod))
            return skillMod;
        
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
        int margin = roll.Result - dc;

        if (margin >= 10) degree = Pf2eDegreeOfSuccess.CriticalSuccess;
        else if (margin >= 0) degree = Pf2eDegreeOfSuccess.Success;
        else if (margin <= -10) degree = Pf2eDegreeOfSuccess.CriticalFailure;
        else degree = Pf2eDegreeOfSuccess.Failure;

        if (roll.HasCritical) // Nat 20 upgrades
        {
            if (degree != Pf2eDegreeOfSuccess.CriticalSuccess)
                degree = (Pf2eDegreeOfSuccess)((int)degree + 1);
        }
        else if (roll.HasComplication) // Nat 1 downgrades
        {
            if (degree != Pf2eDegreeOfSuccess.CriticalFailure)
                degree = (Pf2eDegreeOfSuccess)((int)degree - 1);
        }

        return degree;
    }

    private async Task<string> ResolveAttackAsync(RulesetAction action, ChangeContext context, Pf2eExtension actorStats, List<WorldChange> mutations, CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
            return "Error: No valid target specified for attack.";

        if (target.SystemStats is not Pf2eExtension targetStats)
            return "Error: Target uses incompatible ruleset stats for current ActiveSystem.";
        int ac = targetStats.ArmorClass;
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
            ac = overrideAc;

        int attackBonus = 0;
        if (action.Parameters.TryGetValue("bonus", out var b) && !int.TryParse(b, out attackBonus))
            return $"Error: invalid bonus value '{b}'.";

        string damageDice = action.Parameters.TryGetValue("damageDice", out var dd) ? dd : "1d4";
        
        int damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
            return $"Error: invalid damageBonus value '{db}'.";

        var attackRoll = await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = DiceMechanic.Standard }, ct);
        
        var degree = CalculateDegreeOfSuccess(attackRoll, ac);

        if (degree == Pf2eDegreeOfSuccess.Failure || degree == Pf2eDegreeOfSuccess.CriticalFailure)
            return $"{action.ActionName}: Missed. ({degree}) Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}";

        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);
        int finalDamage = damageRoll.Result;

        if (degree == Pf2eDegreeOfSuccess.CriticalSuccess)
        {
            finalDamage *= 2; // PF2e crits typically double the final calculated damage
        }

        mutations.Add(new HpChange { CharacterId = targetId, Delta = -finalDamage });

        return $"{action.ActionName}: Hit for {finalDamage} damage. ({degree}) Attack {attackRoll.Result} vs AC {ac}.";
    }

    private async Task<string> ResolveSkillCheckAsync(RulesetAction action, Pf2eExtension actorStats, CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
            return "Error: Skill check requires a 'dc' parameter.";

        var skillName = action.Parameters.TryGetValue("skill", out var s) ? s : "Strength";
        int bonus = GetSkillOrAbilityBonus(actorStats, skillName);

        var outcome = await _rollService.RollAsync(new RollRequest { Tag = "skill", Expression = "1d20", Bonus = bonus, Mechanic = DiceMechanic.Standard }, ct);
        
        var degree = CalculateDegreeOfSuccess(outcome, dc);
        
        return $"{action.ActionName} ({skillName}): {degree}. Rolled {outcome.Result} vs DC {dc}. {outcome.Summary}";
    }

    public async Task<float> RollInitiativeAsync(IAsyncDocumentSession session, string characterId, CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null) return 0f;
        var stats = character.SystemStats as Pf2eExtension ?? new Pf2eExtension();
        var request = new RollRequest { Tag = "initiative", Expression = "1d20", Bonus = stats.DexterityMod, Mechanic = DiceMechanic.Standard };
        var outcome = await _rollService.RollAsync(request, ct);
        return outcome.Result;
    }
}
