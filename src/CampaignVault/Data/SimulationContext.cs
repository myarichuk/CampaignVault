using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Context passed to all simulation rules during an AdvanceWorld tick.
/// 
/// Designed to evolve:
/// - Will later carry richer "world pressure" snapshots, region scoping, and initiative queues.
/// - Rules for agency/initiative can use the Session to query additional context (recent events, etc.).
/// - Future: may include a read-only snapshot facade instead of live entities for safer parallel rules.
/// </summary>
public sealed record SimulationContext(
    CampaignTime Time,
    IReadOnlyList<Rumor> ActiveRumors,
    IReadOnlyList<Character> ScheduledNpcs,
    IAsyncDocumentSession Session,
    double DaysPassed,
    string? CampaignName = null,
    IReadOnlyList<Faction>? ActiveFactions = null,
    IReadOnlyList<Quest>? ActiveQuests = null,
    CampaignConfig? Config = null,
    IReadOnlyList<PlotThread>? ActivePlotThreads = null
);
