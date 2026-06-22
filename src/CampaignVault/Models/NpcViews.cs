namespace CampaignVault.Models;

public class NpcContextView
{
    public Character Character { get; set; } = default!;
    public PsychologyProfile Psychology { get; set; } = default!;
    public SocialProfile Social { get; set; } = default!;
    public NeedsProfile Needs { get; set; } = default!;
    public SystemExtension SystemStats { get; set; } = default!;
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
}

/// <summary>
/// Lightweight view returned by GetNpcNeeds for discoverability.
/// </summary>
public class NpcNeedsView
{
    public string CharacterId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public Dictionary<string, float> KnownNeeds { get; set; } = [];
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];
}
