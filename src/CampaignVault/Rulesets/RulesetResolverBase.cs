using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public abstract class RulesetResolverBase<TStats> : IRulesetModule, IActionResolution, ICombatRuleset where TStats : SystemExtension, new()
{
    public abstract RulesetSystem System { get; }
    public IActionResolution Actions => this;
    public ICombatRuleset Combat => this;
    public virtual IEnumerable<IRulesetPressureContributor> PressureContributors => [];

    public async Task<ResolverOutput> ResolveAsync(
        ChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.ActorId, out var actor))
        {
            return new ResolverOutput { Result = ResolverResult.Fail("ActorNotFound", $"Error: Actor '{action.ActorId}' not found.") };
        }

        if (actor.SystemStats is not TStats actorStats)
        {
            return new ResolverOutput { Result = ResolverResult.Fail("IncompatibleRuleset", $"Error: Character uses incompatible ruleset stats for current ActiveSystem.") };
        }

        var mutations = new List<WorldChange>();
        ResolverResult result;

        switch (action.ActionType)
        {
            case RulesetActionType.Attack:
                result = await ResolveAttackAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.SkillCheck:
                result = await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.ContestedCheck:
                result = await ResolveContestedCheckAsync(action, context, actorStats, mutations, ct);
                break;

            default:
                result = ResolverResult.Fail("NotImplemented", $"{System}: Action type {action.ActionType} not yet fully implemented.");
                break;
        }

        return new ResolverOutput
        {
            Mutations = result.Success ? mutations : Array.Empty<WorldChange>(), // Discard mutations on failure
            Result = result
        };
    }

    protected abstract Task<ResolverResult> ResolveAttackAsync(
        RulesetAction action, 
        ChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<ResolverResult> ResolveSkillCheckAsync(
        RulesetAction action, 
        ChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<ResolverResult> ResolveContestedCheckAsync(
        RulesetAction action, 
        ChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    /// <summary>
    /// Session-based initiative roll for direct tool use. 
    /// For combat flows, the preferred path is the Character overload (pre-loaded context).
    /// </summary>
    public async Task<float> RollInitiativeAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session, 
        string characterId, 
        CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null)
        {
            return 0f;
        }

        return await RollInitiativeAsync(character, ct);
    }

    public abstract Task<float> RollInitiativeAsync(
        Character character, 
        CancellationToken ct = default);

    protected DiceMechanic GetMechanicFromParams(Dictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("advantage", out var adv) && bool.TryParse(adv, out var isAdv) && isAdv)
        {
            return DiceMechanic.Advantage;
        }

        if (parameters.TryGetValue("disadvantage", out var dis) && bool.TryParse(dis, out var isDis) && isDis)
        {
            return DiceMechanic.Disadvantage;
        }

        return DiceMechanic.Standard;
    }

    /// <summary>
    /// Folds all active status modifiers matching the given tag into a base value.
    /// Also considers systemic values like Fatigue if applicable.
    /// </summary>
    protected int ApplyAllModifiers(TStats stats, string modifierTag, int baseValue)
    {
        var bonus = 0f;

        // Apply structured status effects
        if (stats.StatusEffects != null)
        {
            foreach (var effect in stats.StatusEffects)
            {
                if (effect.StatModifiers != null && effect.StatModifiers.TryGetValue(modifierTag, out var mod))
                {
                    bonus += mod;
                }
                // Also check generic 'AllRolls' or 'AllChecks' tags if appropriate
                if (modifierTag != "AC" && modifierTag != "Defense") 
                {
                    if (effect.StatModifiers != null && effect.StatModifiers.TryGetValue("AllRolls", out var allRollsMod))
                    {
                        bonus += allRollsMod;
                    }

                    if (modifierTag.Contains("Skill") || modifierTag.Contains("Check"))
                    {
                        if (effect.StatModifiers != null && effect.StatModifiers.TryGetValue("AllChecks", out var allChecksMod))
                        {
                            bonus += allChecksMod;
                        }
                    }
                }
            }
        }

        return baseValue + (int)Math.Floor(bonus);
    }
}
