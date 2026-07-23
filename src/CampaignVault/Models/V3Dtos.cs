using System.Text.Json;

namespace CampaignVault.Models;

public class ToolResult<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public string[]? WorldPressure { get; set; }
    public JsonElement? RetryExample { get; set; }

    public ToolResult() { }
    public ToolResult(bool Success, T? Data = default, string? Summary = null, string? Error = null, string[]? WorldPressure = null, JsonElement? RetryExample = null)
    {
        this.Success = Success;
        this.Data = Data;
        this.Summary = Summary;
        this.Error = Error;
        this.WorldPressure = WorldPressure;
        this.RetryExample = RetryExample;
    }
}

/// <summary>Session-0 signal on GetWorldState: how much of the world is seeded, plus obvious gaps.</summary>
public class SeedCoverageSummary
{
    public int Locations { get; set; }
    public int PcCharacters { get; set; }
    public int Factions { get; set; }
    public int OpenQuests { get; set; }
    public int ActivePlotThreads { get; set; }
    public List<string> Gaps { get; set; } = [];
}

public class WorldStateView
{
    public CampaignTime Time { get; set; } = default!;
    public IEnumerable<RumorSummary> ActiveRumors { get; set; } = [];
    public IEnumerable<Event> RecentEvents { get; set; } = [];
    public LocationSummary? PartyLocation { get; set; }
    public IEnumerable<string> WorldPressure { get; set; } = [];
    public IEnumerable<ActiveQuestSummary>? ActiveQuests { get; set; }
    public IEnumerable<FactionPresenceSummary>? RelevantFactions { get; set; }
    public string? LastKnownTravel { get; set; }
    public IEnumerable<string>? SuggestedCommitExamples { get; set; }

    /// <summary>
    /// Rich pressure items (with optional SuggestedCommitJson). Preferred for structuredContent consumers.
    /// The string WorldPressure contains the formatted display form (including any suggested JSON inline).
    /// </summary>
    public IEnumerable<WorldPressureItem> WorldPressureItems { get; set; } = [];

    /// <summary>
    /// Lightweight session-0 signal: how much of the world has been seeded so far, plus a short
    /// list of obvious gaps. Set by GetWorldState; never blocks — a fresh campaign just shows all
    /// zeros and a longer gap list. See get_help topic=world-building for the seeding guide.
    /// </summary>
    public SeedCoverageSummary? SeedCoverage { get; set; }

    public WorldStateView() { }
    public WorldStateView(CampaignTime time, IEnumerable<RumorSummary> rumors, IEnumerable<Event> events, LocationSummary? location = null, IEnumerable<string>? pressure = null, IEnumerable<ActiveQuestSummary>? activeQuests = null, IEnumerable<FactionPresenceSummary>? relevantFactions = null, string? lastKnownTravel = null, IEnumerable<string>? suggestedCommitExamples = null, IEnumerable<WorldPressureItem>? pressureItems = null)
    {
        Time = time;
        ActiveRumors = rumors;
        RecentEvents = events;
        PartyLocation = location;
        WorldPressure = pressure ?? [];
        ActiveQuests = activeQuests ?? [];
        RelevantFactions = relevantFactions ?? [];
        LastKnownTravel = lastKnownTravel;
        SuggestedCommitExamples = suggestedCommitExamples ?? [];
        WorldPressureItems = pressureItems ?? [];
    }
}

/// <summary>
/// Session briefing: composed aggregate of world state + active party roster.
/// Combines GetWorldState and GetParty into one convenient read for kickoff.
/// </summary>
public class SessionBriefingView
{
    public CampaignTime Time { get; set; } = default!;
    public IEnumerable<RumorSummary> ActiveRumors { get; set; } = [];
    public IEnumerable<Event> RecentEvents { get; set; } = [];
    public LocationSummary? PartyLocation { get; set; }
    public IEnumerable<string> WorldPressure { get; set; } = [];
    public IEnumerable<ActiveQuestSummary>? ActiveQuests { get; set; }
    public IEnumerable<FactionPresenceSummary>? RelevantFactions { get; set; }
    public string? LastKnownTravel { get; set; }
    public IEnumerable<WorldPressureItem> WorldPressureItems { get; set; } = [];
    public SeedCoverageSummary? SeedCoverage { get; set; }
    public IEnumerable<PartyMemberView> Party { get; set; } = [];

    public SessionBriefingView() { }
    public SessionBriefingView(
        WorldStateView worldState,
        IEnumerable<PartyMemberView> party)
    {
        Time = worldState.Time;
        ActiveRumors = worldState.ActiveRumors;
        RecentEvents = worldState.RecentEvents;
        PartyLocation = worldState.PartyLocation;
        WorldPressure = worldState.WorldPressure;
        ActiveQuests = worldState.ActiveQuests;
        RelevantFactions = worldState.RelevantFactions;
        LastKnownTravel = worldState.LastKnownTravel;
        WorldPressureItems = worldState.WorldPressureItems;
        SeedCoverage = worldState.SeedCoverage;
        Party = party;
    }
}
