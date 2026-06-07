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
    string? CampaignName = null,                    // Added for multi-campaign support
    IReadOnlyList<Faction>? ActiveFactions = null,  // Phase 7.1: faction context for FactionEcosystemRule
    IReadOnlyList<Quest>? ActiveQuests = null,      // Phase 7.1: quest context for QuestStalenessRule
    CampaignConfig? Config = null                   // Phase 8.5: configuration for decay
);
