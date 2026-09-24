using CampaignVault.Models;

namespace CampaignVault.Data.Guidance;

/// <summary>
/// Plugin-supplied guidance: short, model-facing hints appended to take_turn responses. Discovered by
/// convention scanning of plugin assemblies (no registration needed). The host stamps each hint with the
/// plugin's assembly name, namespaces its key, and lets at most one plugin hint into a response so core
/// guidance always keeps a slot. Delivery shares the response's guidance character budget.
/// </summary>
public interface IPluginGuidanceContributor
{
    /// <summary>Called once per guidance collection. Return empty on quiet turns; exceptions are swallowed.</summary>
    Task<IEnumerable<PluginGuidanceHint>> EvaluateAsync(IGuidanceContext ctx, CancellationToken ct = default);
}

/// <summary>
/// Raven-free, read-only view of the turn a guidance contributor is evaluated for. Mirrors
/// <see cref="ChangeHandlers.IChangeContext"/>: plugins see models, never the host's document session.
/// </summary>
public interface IGuidanceContext
{
    string CampaignName { get; }
    CampaignTime? Time { get; }
    CampaignConfig? Config { get; }

    /// <summary>Character IDs surfaced in this response. Empty when the caller has no character context.</summary>
    IReadOnlyList<string> PartyCharacterIds { get; }

    /// <summary>
    /// Changes committed by this turn, including engine ambient deltas. The edge-trigger signal: e.g. a
    /// <see cref="ModeTransitionChange"/> entering your mode means the next verbs will be yours.
    /// </summary>
    IReadOnlyList<WorldChange> AppliedChanges { get; }
}

/// <summary>
/// A plugin guidance hint. <paramref name="Key"/> only needs to be unique within the plugin; the host
/// prefixes it with the plugin id. Higher <paramref name="Priority"/> wins when hints compete for budget.
/// Hints are not deduplicated across turns today, so emit them on an edge (something just happened),
/// not on a level (something is still true).
/// </summary>
public sealed record PluginGuidanceHint(string Key, string Text, int Priority = 0)
{
    /// <summary>Copy-paste JSON example, ideally ≤ 200 chars. Counts toward the guidance budget.</summary>
    public string? Example { get; init; }

    /// <summary>Days before the hint may repeat once the host tracks delivery. Null = once.</summary>
    public int? RepeatAfterDays { get; init; }
}
