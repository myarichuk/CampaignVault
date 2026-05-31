using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class Dnd5eRulesetResolver : IRulesetResolver
{
    private readonly IRollService _rollService;

    public Dnd5eRulesetResolver(IRollService rollService)
    {
        _rollService = rollService;
    }

    public RulesetSystem System => RulesetSystem.Dnd5e;

    public async Task<ResolverOutput> ResolveAsync(
        ChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.ActorId, out var actor))
        {
            return new ResolverOutput { Result = new ResolverResult { Narrative = $"Error: Actor '{action.ActorId}' not found." } };
        }

        if (actor.SystemStats is not Dnd5eExtension actorStats)
        {
            return new ResolverOutput { Result = new ResolverResult { Narrative = $"Error: Character uses incompatible ruleset stats for current ActiveSystem." } };
        }
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

            case RulesetActionType.ContestedCheck:
                narrative = await ResolveContestedCheckAsync(action, context, actorStats, ct);
                break;

            default:
                narrative = $"D&D 5e: Action type {action.ActionType} not yet fully implemented.";
                break;
        }

        return new ResolverOutput
        {
            Mutations = mutations,
            Result = new ResolverResult { Narrative = narrative }
        };
    }

    private DiceMechanic GetMechanicFromParams(Dictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("advantage", out var adv) && bool.TryParse(adv, out var isAdv) && isAdv)
            return DiceMechanic.Advantage;
        if (parameters.TryGetValue("disadvantage", out var dis) && bool.TryParse(dis, out var isDis) && isDis)
            return DiceMechanic.Disadvantage;
        return DiceMechanic.Standard;
    }

    private int GetSkillOrAbilityBonus(Dnd5eExtension stats, string name)
    {
        if (stats.SkillModifiers.TryGetValue(name, out var skillMod))
            return skillMod;
        
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

    private async Task<string> ResolveAttackAsync(
        RulesetAction action, 
        ChangeContext context, 
        Dnd5eExtension actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
            return "Error: No valid target specified for attack.";

        if (target.SystemStats is not Dnd5eExtension targetStats)
            return "Error: Target uses incompatible ruleset stats for current ActiveSystem.";
        int ac = targetStats.ArmorClass;
        ac = targetStats.ApplyModifiers("AC", ac);
        
        // AC override
        if (action.Parameters.TryGetValue("ac", out var acStr) && int.TryParse(acStr, out var overrideAc))
            ac = overrideAc;

        int attackBonus = 0;
        if (action.Parameters.TryGetValue("bonus", out var b) && !int.TryParse(b, out attackBonus))
            return $"Error: invalid bonus value '{b}'.";
        attackBonus = actorStats.ApplyModifiers("AttackRoll", attackBonus);

        string damageDice = action.Parameters.TryGetValue("damageDice", out var dd) ? dd : "1d4"; // Unarmed default
        
        int damageBonus = 0;
        if (action.Parameters.TryGetValue("damageBonus", out var db) && !int.TryParse(db, out damageBonus))
            return $"Error: invalid damageBonus value '{db}'.";
        damageBonus = actorStats.ApplyModifiers("DamageRoll", damageBonus);

        var mechanic = GetMechanicFromParams(action.Parameters);

        var attackRoll = await _rollService.RollAsync(new RollRequest { Tag = "attack", Expression = "1d20", Bonus = attackBonus, Mechanic = mechanic }, ct);
        var damageRoll = await _rollService.RollAsync(new RollRequest { Tag = "damage", Expression = damageDice, Bonus = damageBonus, Mechanic = DiceMechanic.Standard }, ct);

        bool isHit = false;
        bool isCrit = attackRoll.HasCritical; // Nat 20

        if (isCrit) isHit = true;
        else if (attackRoll.HasComplication) isHit = false; // Nat 1
        else if (attackRoll.Result >= ac) isHit = true;

        if (!isHit)
            return $"{action.ActionName}: Missed. Attack {attackRoll.Result} vs AC {ac}. {attackRoll.Summary}";

        // Handle critical damage (double dice)
        int finalDamage = damageRoll.Result;
        string critMsg = "";
        if (isCrit)
        {
            // Roll damage dice again for crit
            var critDmg = await _rollService.RollAsync(new RollRequest { Tag = "critDamage", Expression = damageDice, Mechanic = DiceMechanic.Standard }, ct);
            finalDamage += critDmg.Result;
            critMsg = $" CRITICAL HIT! Added {critDmg.Result} extra damage.";
        }

        mutations.Add(new HpChange
        {
            CharacterId = targetId,
            Delta = -finalDamage
        });

        return $"{action.ActionName}: Hit for {finalDamage} damage. (Attack {attackRoll.Result} vs AC {ac}).{critMsg}";
    }

    private async Task<string> ResolveSkillCheckAsync(RulesetAction action, Dnd5eExtension actorStats, CancellationToken ct)
    {
        if (!action.Parameters.TryGetValue("dc", out var dcStr) || !int.TryParse(dcStr, out var dc))
            return "Error: Skill check requires a 'dc' parameter.";

        var skillName = action.Parameters.TryGetValue("skill", out var s) ? s : "Strength";
        int bonus = GetSkillOrAbilityBonus(actorStats, skillName);
        bonus = actorStats.ApplyModifiers("SkillCheck", bonus);
        bonus = actorStats.ApplyModifiers(skillName, bonus);
        var mechanic = GetMechanicFromParams(action.Parameters);

        var outcome = await _rollService.RollAsync(new RollRequest
        {
            Tag = "skill",
            Expression = "1d20",
            Bonus = bonus,
            Mechanic = mechanic
        }, ct);

        bool isSuccess = outcome.Result >= dc;
        string resultStr = isSuccess ? "Success" : "Failure";
        
        return $"{action.ActionName} ({skillName}): {resultStr}. Rolled {outcome.Result} vs DC {dc}. {outcome.Summary}";
    }

    private async Task<string> ResolveContestedCheckAsync(RulesetAction action, ChangeContext context, Dnd5eExtension actorStats, CancellationToken ct)
    {
        var targetId = action.TargetIds.FirstOrDefault();
        if (targetId == null || !context.Characters.TryGetValue(targetId, out var target))
            return "Error: No valid target specified for contested check.";

        if (target.SystemStats is not Dnd5eExtension targetStats)
            return "Error: Target uses incompatible ruleset stats for current ActiveSystem.";

        var actorSkill = action.Parameters.TryGetValue("skill", out var as_name) ? as_name : "Strength";
        var targetSkill = action.Parameters.TryGetValue("targetSkill", out var ts_name) ? ts_name : actorSkill;

        int actorBonus = GetSkillOrAbilityBonus(actorStats, actorSkill);
        actorBonus = actorStats.ApplyModifiers("SkillCheck", actorBonus);
        actorBonus = actorStats.ApplyModifiers(actorSkill, actorBonus);

        int targetBonus = GetSkillOrAbilityBonus(targetStats, targetSkill);
        targetBonus = targetStats.ApplyModifiers("SkillCheck", targetBonus);
        targetBonus = targetStats.ApplyModifiers(targetSkill, targetBonus);

        var actorRoll = await _rollService.RollAsync(new RollRequest { Tag = "actor", Expression = "1d20", Bonus = actorBonus, Mechanic = GetMechanicFromParams(action.Parameters) }, ct);
        var targetRoll = await _rollService.RollAsync(new RollRequest { Tag = "target", Expression = "1d20", Bonus = targetBonus, Mechanic = DiceMechanic.Standard }, ct);

        // Ties usually favor the status quo or defender, but we'll assume higher wins, tie = defender wins.
        bool actorWins = actorRoll.Result > targetRoll.Result; 
        string resultStr = actorWins ? "Actor Wins" : "Target Wins";

        return $"{action.ActionName}: {resultStr}. Actor rolled {actorRoll.Result} ({actorSkill}), Target rolled {targetRoll.Result} ({targetSkill}).";
    }

    public async Task<float> RollInitiativeAsync(
        IAsyncDocumentSession session, 
        string characterId, 
        CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null) return 0f;

        var stats = character.SystemStats as Dnd5eExtension ?? new Dnd5eExtension();
        int dexMod = stats.GetAbilityModifier(stats.Dexterity);
        dexMod = stats.ApplyModifiers("Initiative", dexMod);

        var request = new RollRequest 
        { 
            Tag = "initiative", 
            Expression = "1d20", 
            Bonus = dexMod,
            Mechanic = DiceMechanic.Standard 
        };
        
        var outcome = await _rollService.RollAsync(request, ct);
        return outcome.Result;
    }
}
