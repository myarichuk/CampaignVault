using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class ResolverResult
{
    public bool Success { get; init; } = true;
    public string? ErrorCode { get; init; }
    public string Narrative { get; init; } = string.Empty;

    public static ResolverResult Ok(string narrative) => new() { Success = true, Narrative = narrative };
    public static ResolverResult Fail(string errorCode, string narrative) => new() { Success = false, ErrorCode = errorCode, Narrative = narrative };
}

public class ResolverOutput
{
    public IReadOnlyList<WorldChange> Mutations { get; init; } = [];
    public ResolverResult Result { get; init; } = ResolverResult.Ok(string.Empty);
}

public interface IActionResolution
{
    Task<ResolverOutput> ResolveAsync(
        ChangeContext context,
        RulesetAction action,
        CancellationToken ct = default);
}

public interface ICombatRuleset
{
    Task<float> RollInitiativeAsync(
        IAsyncDocumentSession session,
        string characterId,
        CancellationToken ct = default);

    Task<float> RollInitiativeAsync(
        Character character,
        CancellationToken ct = default);
}

public interface IRulesetPressureContributor : IPressureContributor;

public interface IRulesetModule
{
    RulesetSystem System { get; }
    IActionResolution Actions { get; }
    ICombatRuleset Combat { get; }
    IEnumerable<IRulesetPressureContributor> PressureContributors { get; }
}