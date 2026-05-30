namespace CampaignVault.Models;

public class SceneView
{
    public Location Location { get; set; } = default!;
    /// <summary>
    /// Lightweight summaries of NPCs currently present (driven by simulated Schedule + Current* state).
    /// Much smaller than full Character objects, focused on what an LLM DM actually needs for roleplay.
    /// </summary>
    public IEnumerable<NpcPresenceSummary> PresentNPCs { get; set; } = [];
    public IEnumerable<RumorSummary> LocalRumors { get; set; } = [];
    public IEnumerable<Item> VisibleItems { get; set; } = [];
    public IEnumerable<Event> RecentEvents { get; set; } = [];
}

/// <summary>
/// Lightweight view of an NPC for scene exploration.
/// Contains just enough psychological + situational data for the DM to roleplay without dumping entire documents.
/// </summary>
public record NpcPresenceSummary(
    string Id,
    string Name,
    string? CurrentActivity,
    string? CurrentMood,
    Dictionary<string, float> TopNeeds,
    /// <summary>
    /// Known needs for this NPC (key → current value). The vocabulary is open — the LLM is encouraged to invent new narrative-appropriate needs.
    /// </summary>
    Dictionary<string, float> KnownNeeds,
    /// <summary>
    /// Optional descriptions for the needs (when the world-builder or LLM has provided them).
    /// </summary>
    Dictionary<string, string> NeedDescriptors,
    string? BehavioralSummary = null,
    string? Notes = null
);

public class CommitResult
{
    public bool Success { get; set; } = true;
    public int ChangesProcessed { get; set; }
    public List<string> Summary { get; set; } = [];
}

public class AdvanceResult
{
    public CampaignTime NewTime { get; set; } = default!;
    public List<string> SimulatorEvents { get; set; } = [];
}
