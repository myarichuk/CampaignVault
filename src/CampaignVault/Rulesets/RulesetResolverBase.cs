using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Rulesets;

public abstract class RulesetResolverBase<TStats> : IRulesetModule, IActionResolution, ICombatRuleset where TStats : SystemExtension, new()
{
    public abstract RulesetSystem System { get; }
    public IActionResolution Actions => this;
    public ICombatRuleset Combat => this;
    public virtual ICharacterBootstrapPipeline Bootstrap => NullCharacterBootstrapPipeline.Instance;
    public virtual IEnumerable<IRulesetPressureContributor> PressureContributors => [];

    public async Task<ResolverOutput> ResolveAsync(
        ChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.ActorId, out var actor))
        {
            return new ResolverOutput
            {
                Result = ResolverResult.Fail("ActorNotFound",
                    $"Error: Actor '{action.ActorId}' not found or not visible in campaign '{context.CampaignName}'.")
            };
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && !CampaignEntityVisibility.IsVisibleInCampaign(actor.CampaignName, context.CampaignName))
        {
            CampaignEntityVisibility.TryGetInvisibilityReason(actor, context.CampaignName, out var reason);
            return new ResolverOutput
            {
                Result = ResolverResult.Fail("InvalidInput",
                    $"Error: Actor '{action.ActorId}' is not available in campaign '{context.CampaignName}'. {reason}")
            };
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
                await WeaponParameterResolver.ApplyHeldWeaponDefaultsAsync(action, context, ct);
                result = await ResolveAttackAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.SkillCheck:
                result = await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.ContestedCheck:
                result = await ResolveContestedCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.SavingThrow:
                result = await ResolveSavingThrowAsync(action, context, actorStats, mutations, ct);
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

    protected abstract Task<ResolverResult> ResolveSavingThrowAsync(
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

    protected DiceMechanic GetMechanicFromAction(RulesetAction action)
    {
        // 1. Check explicit AdvantageState first (the modern way)
        if (action.AdvantageState == AdvantageState.Advantage) return DiceMechanic.Advantage;
        if (action.AdvantageState == AdvantageState.Disadvantage) return DiceMechanic.Disadvantage;

        // 2. Fall back to legacy Parameters for backward compatibility
        return GetMechanicFromParams(action.Parameters);
    }

    protected static bool TryGetParameter(
        Dictionary<string, string> parameters,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out value!))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

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
    protected int ApplyAllModifiers(TStats stats, int baseValue, params string[] modifierTags)
    {
        var bonus = 0f;
        if (stats.StatusEffects != null)
        {
            foreach (var effect in stats.StatusEffects)
            {
                if (effect.StatModifiers == null) continue;

                var appliedAllRolls = false;
                var appliedAllChecks = false;
                var appliedAllSaves = false;

                foreach (var tag in modifierTags)
                {
                    if (effect.StatModifiers.TryGetValue(tag, out var directMod))
                    {
                        bonus += directMod;
                    }

                    if (tag != "AC" && tag != "Defense")
                    {
                        if (!appliedAllRolls && effect.StatModifiers.TryGetValue("AllRolls", out var allRollsMod))
                        {
                            bonus += allRollsMod;
                            appliedAllRolls = true;
                        }

                        var lowerTag = tag.ToLowerInvariant();
                        if (lowerTag.Contains("check") || lowerTag.Contains("skill"))
                        {
                            if (!appliedAllChecks && effect.StatModifiers.TryGetValue("AllChecks", out var allChecksMod))
                            {
                                bonus += allChecksMod;
                                appliedAllChecks = true;
                            }
                        }

                        if (lowerTag.Contains("save") || lowerTag.Contains("saving"))
                        {
                            if (!appliedAllSaves && effect.StatModifiers.TryGetValue("AllSaves", out var allSavesMod))
                            {
                                bonus += allSavesMod;
                                appliedAllSaves = true;
                            }
                        }
                    }
                }
            }
        }
        return baseValue + (int)Math.Floor(bonus);
    }
}
