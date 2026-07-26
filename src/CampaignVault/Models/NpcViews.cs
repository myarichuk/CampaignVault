namespace CampaignVault.Models;

/// <summary>
/// Minimal plot thread summary embedded in entity detail responses.
/// Payload restricted to: id, title, state, tensionLevel only.
/// </summary>
public record PlotThreadMinimal(
    string Id,
    string Title,
    PlotThreadState State,
    int TensionLevel);

public class NpcContextView
{
    public Character Character { get; set; } = null!;
    public PsychologyProfile Psychology { get; set; } = null!;
    public SocialProfile Social { get; set; } = null!;
    public NeedsProfile Needs { get; set; } = null!;
    public SystemExtension SystemStats { get; set; } = null!;
    public IEnumerable<Event> RecentInteractions { get; set; } = [];
    public string? BehavioralSummary { get; set; }

    /// <summary>
    /// All known needs for this NPC with their current values. The needs system is intentionally open-ended.
    /// </summary>
    public Dictionary<string, float> KnownNeeds { get; set; } = [];

    /// <summary>
    /// Human/LLM-readable descriptions for the needs (seeded by world-builder or previous LLM actions).
    /// </summary>
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];

    public double BehavioralTension { get; set; }
    public TensionBreakdown? TensionComponents { get; set; }
    public List<InitiativeCandidate> ActiveInitiatives { get; set; } = [];
    public List<MemoryNode> RelevantMemories { get; set; } = [];
    public List<ItemSummaryView>? Equipped { get; set; }
    public List<ItemSummaryView>? Carried { get; set; }

    /// <summary>Advisory-only "whose move is it" hint for this NPC — never a hard gate.</summary>
    public TurnIntentSignal? TurnIntent { get; set; }

    /// <summary>Plot threads associated with this NPC (referenced via thread-level or clue-level involvedEntityIds).</summary>
    public List<PlotThreadMinimal> AssociatedPlotThreads { get; set; } = [];
}

/// <summary>
/// Lightweight view returned by GetNpcNeeds for discoverability.
/// </summary>
public class NpcNeedsView
{
    public string CharacterId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Dictionary<string, float> KnownNeeds { get; set; } = [];
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];
}

/// <summary>
/// Lightweight summary of an NPC for quick lookups (get_npc_summary).
/// Slices the heavier NpcContextView to essential roleplay data: name, behavior, needs, and gear.
/// </summary>
public class NpcSummaryView
{
    public string CharacterId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string CurrentAppearance { get; set; } = null!;
    public string? BehavioralSummary { get; set; }
    public Dictionary<string, float> KnownNeeds { get; set; } = [];
    public List<ItemSummaryView>? Equipped { get; set; }
    public List<ItemSummaryView>? Carried { get; set; }
}
