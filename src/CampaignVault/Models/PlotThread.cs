namespace CampaignVault.Models;

public class PlotThread : ICampaignScopedEntity, IArchivable
{
    public string Id { get; set; } = null!;
    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }

    public string BuildEmbeddingText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title);
        if (!string.IsNullOrWhiteSpace(Summary)) parts.Add(Summary);
        if (!string.IsNullOrWhiteSpace(ResolutionCondition)) parts.Add(ResolutionCondition);
        if (ForeshadowingHooks.Count > 0) parts.AddRange(ForeshadowingHooks.Where(h => !string.IsNullOrWhiteSpace(h)));
        if (Clues.Count > 0) parts.AddRange(Clues.Select(c => c.Description).Where(d => !string.IsNullOrWhiteSpace(d)));
        if (!string.IsNullOrWhiteSpace(DmNotes)) parts.Add(DmNotes);
        return string.Join('\n', parts);
    }

    public string Title { get; set; } = null!;
    public string? Summary { get; set; }
    public PlotThreadState State { get; set; } = PlotThreadState.Active;

    /// <summary>
    /// Tension 0–100. Auto-escalated by PlotThreadEvolutionRule (+5/day Active, +10/day Escalating).
    /// Active→Escalating at 60, Escalating→Climax at 80.
    /// </summary>
    public int TensionLevel { get; set; } = 0;

    public List<PlotClue> Clues { get; set; } = [];
    public List<string> InvolvedEntityIds { get; set; } = [];
    public string? ResolutionCondition { get; set; }
    public List<string> ForeshadowingHooks { get; set; } = [];

    /// <summary>
    /// Flattened union of thread-level InvolvedEntityIds and all clue-level InvolvedEntityIds.
    /// Used for indexed reverse lookup: "which plots reference this entity?"
    /// Computed on serialize to avoid storage duplication.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> AllInvolvedEntityIds
    {
        get
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            if (InvolvedEntityIds != null)
            {
                foreach (var id in InvolvedEntityIds)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        result.Add(id);
                }
            }

            if (Clues != null)
            {
                foreach (var clue in Clues)
                {
                    if (clue.InvolvedEntityIds != null)
                    {
                        foreach (var id in clue.InvolvedEntityIds)
                        {
                            if (!string.IsNullOrWhiteSpace(id))
                                result.Add(id);
                        }
                    }
                }
            }

            return result.ToList();
        }
    }

    /// <summary>DM-only notes. Never visible to players.</summary>
    public string? DmNotes { get; set; }

    public int DayCreated { get; set; }
    public int LastUpdatedDay { get; set; }
    public int? DeadlineDay { get; set; }
    public int? ClimaxEnteredDay { get; set; }

    /// <summary>When true, players are aware this arc exists. Usually false — most threads are hidden DM scaffolding.</summary>
    public bool IsPlayerVisible { get; set; }

    public string? CampaignName { get; set; }

    /// <summary>
    /// When true, hidden from default search/scene results (soft delete). Does not remove history.
    /// </summary>
    public bool IsArchived { get; set; }
}

public record PlotClue(
    string Id,
    string Description,
    bool IsDiscovered = false,
    int? DiscoveredOnDay = null,
    List<string>? InvolvedEntityIds = null
)
{
    public PlotClue() : this(Id: string.Empty, Description: string.Empty) { }
}

public enum PlotThreadState
{
    /// <summary>Seeded but not yet relevant. Simulation does not escalate tension.</summary>
    Dormant,
    /// <summary>In play. Tension rises +5/day until resolved or player-engaged.</summary>
    Active,
    /// <summary>Tension ≥ 60. Consequences imminent. Tension rises +10/day.</summary>
    Escalating,
    /// <summary>Tension ≥ 80. Crisis point — resolution or disaster must happen soon.</summary>
    Climax,
    /// <summary>Arc concluded (player success or natural end).</summary>
    Resolved,
    /// <summary>Arc dropped intentionally by DM (world moved on, story pivoted).</summary>
    Abandoned
}
