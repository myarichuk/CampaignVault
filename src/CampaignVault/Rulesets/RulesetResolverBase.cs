using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

public abstract class RulesetResolverBase<TStats> : IRulesetModule, IActionResolution, ICombatRuleset where TStats : SystemExtension, new()
{
    private readonly WeaponDefinitionProvider? _weaponDefs;

    protected RulesetResolverBase(WeaponDefinitionProvider? weaponDefs = null)
    {
        _weaponDefs = weaponDefs;
    }

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
        if (!context.Characters.TryGetValue(action.CharacterId, out var actor))
        {
            return new ResolverOutput
            {
                Result = ResolverResult.Fail("ActorNotFound",
                    $"Error: Character '{action.CharacterId}' not found or not visible in campaign '{context.CampaignName}'.")
            };
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && !CampaignEntityVisibility.IsVisibleInCampaign(actor.CampaignName, context.CampaignName))
        {
            CampaignEntityVisibility.TryGetInvisibilityReason(actor, context.CampaignName, out var reason);
            return new ResolverOutput
            {
                Result = ResolverResult.Fail("InvalidInput",
                    $"Error: Character '{action.CharacterId}' is not available in campaign '{context.CampaignName}'. {reason}")
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
                await WeaponParameterResolver.ApplyHeldWeaponDefaultsAsync(action, context, ct, _weaponDefs, System);
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

            case RulesetActionType.Spell:
                result = await ResolveSpellAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.OpposedCheck:
                result = await ResolveContestedCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.Recovery:
            case RulesetActionType.UseItem:
                result = await ResolveRecoveryAsync(action, context, actorStats, mutations, ct);
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

    protected virtual async Task<ResolverResult> ResolveSpellAsync(
        RulesetAction action,
        ChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var mode = SpellResolutionHelper.InferMode(action);

        switch (mode)
        {
            case SpellResolutionMode.Attack:
                return await ResolveAttackAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Save:
                return await ResolveSpellSaveAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Check:
                return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Heal:
                return await ResolveSpellHealAsync(action, context, actorStats, mutations, ct);

            case SpellResolutionMode.Utility:
            default:
                return await ResolveSpellUtilityAsync(action, context, actorStats, mutations, ct);
        }
    }

    protected virtual Task<ResolverResult> ResolveSpellSaveAsync(
        RulesetAction action,
        ChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct) =>
        Task.FromResult(ResolverResult.Fail(
            "NotImplemented",
            $"{System}: Spell save resolution requires a ruleset-specific implementation."));

    protected virtual async Task<ResolverResult> ResolveSpellUtilityAsync(
        RulesetAction action,
        ChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        if (action.Parameters.ContainsKey("dc"))
        {
            return await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
        }

        return ResolverResult.Ok(
            $"{action.ActionName}: Non-combat utility spell — no DC supplied. Narrate the outcome; commit status/effects separately if needed.");
    }

    protected virtual async Task<ResolverResult> ResolveSpellHealAsync(
        RulesetAction action,
        ChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        var targets = action.TargetIds.Count > 0 ? action.TargetIds : [action.CharacterId];
        if (!TryGetParameter(action.Parameters, out var healDice, "healDice", "damageDice"))
        {
            healDice = "1d4";
        }

        var healBonus = 0;
        if (action.Parameters.TryGetValue("healBonus", out var hb) && !int.TryParse(hb, out healBonus))
        {
            return ResolverResult.Fail("InvalidParameter", $"Error: invalid healBonus value '{hb}'.");
        }

        var narratives = new List<string>();
        foreach (var targetId in targets)
        {
            if (!context.Characters.TryGetValue(targetId, out var target))
            {
                return ResolverResult.Fail("InvalidTarget", $"Error: Target '{targetId}' not found for healing spell.");
            }

            var healRoll = await RollHealAmountAsync(healDice, healBonus, ct);
            mutations.Add(new HpChange { CharacterId = targetId, Delta = healRoll });
            narratives.Add($"{action.ActionName} heals {target.Name} for {healRoll} HP.");
        }

        return ResolverResult.Ok(string.Join(" | ", narratives));
    }

    protected virtual async Task<ResolverResult> ResolveRecoveryAsync(
        RulesetAction action,
        ChangeContext context,
        TStats actorStats,
        List<WorldChange> mutations,
        CancellationToken ct)
    {
        action.ActionCategory = action.ActionCategory == default ? ActionCategory.Survival : action.ActionCategory;
        return await ResolveSpellHealAsync(action, context, actorStats, mutations, ct);
    }

    protected virtual async Task<int> RollHealAmountAsync(string healDice, int healBonus, CancellationToken ct)
    {
        var rollService = GetRollService();
        if (rollService is null)
        {
            return Math.Max(1, healBonus);
        }

        var outcome = await rollService.RollAsync(new RollRequest
        {
            Tag = "heal",
            Expression = healDice,
            Bonus = healBonus,
            Mechanic = DiceMechanic.Standard,
        }, ct);
        return Math.Max(0, outcome.Result);
    }

    protected virtual IRollService? GetRollService() => null;

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

    public virtual IReadOnlyDictionary<string, int> GetTurnActionBudget(Character character)
    {
        // "reaction" is deliberately not a budget key: reaction gating is handled entirely via
        // CombatantState.ReactionAvailable (see TryConsumeActionSlot's IsReaction early-return below).
        return new Dictionary<string, int>
        {
            { "action", 1 },
            { "bonus", 1 }
        };
    }

    public virtual bool TryConsumeActionSlot(CombatantState state, RulesetAction action, out string? errorReason)
    {
        errorReason = null;

        if (action.IsReaction)
        {
            return true;
        }

        if (state.ActionBudget.Count == 0)
        {
            return true;
        }

        var slot = action.Parameters.TryGetValue("bonusAction", out var bonusStr) && bool.TryParse(bonusStr, out var isBonus) && isBonus
            ? "bonus"
            : "action";

        if (!state.ActionBudget.TryGetValue(slot, out var remaining) || remaining <= 0)
        {
            errorReason = $"No {slot} remaining this turn.";
            return false;
        }

        state.ActionBudget[slot]--;
        return true;
    }

    public virtual bool EnforcesRange => true;
}
