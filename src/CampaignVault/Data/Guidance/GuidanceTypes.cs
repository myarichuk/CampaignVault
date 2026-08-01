using System.ComponentModel;
using CampaignVault.Data.Pressure;

namespace CampaignVault.Data.Guidance;

/// <summary>
/// A single guidance hint with once-per-campaign delivery and optional repeat-after logic.
/// </summary>
public sealed record GuidanceHint(
    string Key,
    string Text,
    GuidanceTrigger Trigger,
    int Priority = 0)
{
    /// <summary>Copy-paste JSON example, ≤ 200 chars.</summary>
    public string? Example { get; init; }

    /// <summary>Days before hint can be delivered again. Null = strictly once.</summary>
    public int? RepeatAfterDays { get; init; }
}

public enum GuidanceTrigger
{
    [Description("Campaign created")]
    FirstCommit = 0,

    [Description("Initial world-building")]
    FirstWorldBuild = 1,

    [Description("Combat started")]
    CombatStarted = 2,

    [Description("Spellcasting involved")]
    Spellcasting = 3,

    [Description("Item damage")]
    ItemDamage = 4,

    [Description("Plot thread staleness")]
    PlotThreadStaleness = 5,

    [Description("Incomplete system stats")]
    IncompleteSystemStats = 6,

    [Description("Rest or travel")]
    RestAndTravel = 7,

    [Description("Narrative focus")]
    NarrativeFocus = 8,

    [Description("Time recording")]
    TimeRecording = 9,
}

/// <summary>
/// Pluggable contributor of guidance hints, evaluated per tool response.
/// </summary>
public interface IGuidanceContributor
{
    PressureScope Scope { get; }
    int Order { get; }

    Task<IEnumerable<GuidanceHint>> EvaluateAsync(
        PressureContext ctx,
        CancellationToken ct = default);
}

/// <summary>
/// Collects, deduplicates, and caps guidance hints by budget.
/// </summary>
public interface IGuidanceOrchestrator
{
    Task<IReadOnlyList<GuidanceHint>> CollectAsync(
        PressureScope scope,
        PressureContext ctx,
        bool ignoreLedger = false,
        CancellationToken ct = default);
}
