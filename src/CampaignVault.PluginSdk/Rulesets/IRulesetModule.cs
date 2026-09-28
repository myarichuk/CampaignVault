using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Rulesets;

public class ResolverResult
{
    public bool Success { get; init; } = true;
    public string? ErrorCode { get; init; }
    public string Narrative { get; init; } = string.Empty;

    /// <summary>Skill checks: the rolled total and the skill, so the engine can resolve what the roll finds
    /// (hidden content, disarming a trap) the same way it resolves the roll itself.</summary>
    public int? RollTotal { get; init; }

    public string? Skill { get; init; }

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
        IChangeContext context,
        RulesetAction action,
        CancellationToken ct = default);
}

public interface ICombatRuleset
{
    Task<float> RollInitiativeAsync(
        Character character,
        CancellationToken ct = default);

    /// <summary>
    /// Ruleset-defined action-economy slots and their per-turn counts.
    /// Empty dict means no gating (unrestricted, as in Narrative).
    /// 5e example: {"action":1,"bonus":1,"reaction":1,"movement":1}
    /// PF2e example: {"actions":3,"reaction":1}
    /// </summary>
    IReadOnlyDictionary<string, int> GetTurnActionBudget(Character character);

    /// <summary>
    /// Attempts to consume the correct slot(s) in state.ActionBudget for the given action.
    /// Returns false + errorReason if insufficient budget remains (e.g. "No action remaining this turn.").
    /// Ruleset-specific: e.g. 5e Attack costs "action" (or "bonus" if parameters["bonusAction"]="true");
    /// PF2e Strike costs 1 of 3 "actions" (or more via parameters["actionCost"]).
    /// </summary>
    bool TryConsumeActionSlot(CombatantState state, RulesetAction action, out string? errorReason);

    /// <summary>Whether this ruleset enforces spatial range/AoE gating at all (false lets Narrative opt out entirely).</summary>
    bool EnforcesRange { get; }
}

/// <summary>
/// Per-system ruleset facade. Implement this (usually via one class implementing
/// <see cref="IActionResolution"/> and <see cref="ICombatRuleset"/> and exposing itself
/// through <see cref="Actions"/> / <see cref="Combat"/>) to author a full ruleset
/// out-of-tree: build against the PluginSdk package only, drop the DLL + plugin.json
/// under the host's Plugins/ folder, and restart the host.
/// System-specific read-side pressures are intentionally not part of this contract:
/// pressure contexts carry the host's live database session, so implement
/// <see cref="Data.Guidance.IPluginGuidanceContributor"/> and/or
/// <see cref="Data.Context.IPluginContextContributor"/> instead — the host surfaces
/// those on the same read paths.
/// </summary>
public interface IRulesetModule
{
    string System { get; }
    IActionResolution Actions { get; }
    ICombatRuleset Combat { get; }
    ICharacterBootstrapPipeline Bootstrap { get; }
}
