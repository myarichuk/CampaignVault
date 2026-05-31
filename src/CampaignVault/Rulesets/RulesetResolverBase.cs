using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public abstract class RulesetResolverBase<TStats> : IRulesetResolver where TStats : SystemExtension, new()
{
    public abstract RulesetSystem System { get; }

    public async Task<ResolverOutput> ResolveAsync(
        ChangeContext context, 
        RulesetAction action, 
        CancellationToken ct = default)
    {
        if (!context.Characters.TryGetValue(action.ActorId, out var actor))
        {
            return new ResolverOutput { Result = new ResolverResult { Narrative = $"Error: Actor '{action.ActorId}' not found." } };
        }

        if (actor.SystemStats is not TStats actorStats)
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
                narrative = await ResolveSkillCheckAsync(action, context, actorStats, mutations, ct);
                break;

            case RulesetActionType.ContestedCheck:
                narrative = await ResolveContestedCheckAsync(action, context, actorStats, mutations, ct);
                break;

            default:
                narrative = $"{System}: Action type {action.ActionType} not yet fully implemented.";
                break;
        }

        return new ResolverOutput
        {
            Mutations = mutations,
            Result = new ResolverResult { Narrative = narrative }
        };
    }

    protected abstract Task<string> ResolveAttackAsync(
        RulesetAction action, 
        ChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<string> ResolveSkillCheckAsync(
        RulesetAction action, 
        ChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    protected abstract Task<string> ResolveContestedCheckAsync(
        RulesetAction action, 
        ChangeContext context, 
        TStats actorStats, 
        List<WorldChange> mutations, 
        CancellationToken ct);

    public async Task<float> RollInitiativeAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session, 
        string characterId, 
        CancellationToken ct = default)
    {
        var character = await session.LoadAsync<Character>(characterId, ct);
        if (character == null) return 0f;
        return await RollInitiativeAsync(character, ct);
    }

    public abstract Task<float> RollInitiativeAsync(
        Character character, 
        CancellationToken ct = default);

    protected DiceMechanic GetMechanicFromParams(Dictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("advantage", out var adv) && bool.TryParse(adv, out var isAdv) && isAdv)
            return DiceMechanic.Advantage;
        if (parameters.TryGetValue("disadvantage", out var dis) && bool.TryParse(dis, out var isDis) && isDis)
            return DiceMechanic.Disadvantage;
        return DiceMechanic.Standard;
    }
}
