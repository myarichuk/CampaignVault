namespace CampaignVault.Models;

public class WorldEvent : ICampaignScopedEntity, IArchivable
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
        if (!string.IsNullOrWhiteSpace(Description)) parts.Add(Description);
        if (!string.IsNullOrWhiteSpace(DmNotes)) parts.Add(DmNotes);
        return string.Join('\n', parts);
    }

    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? ActorId { get; set; }
    public List<string> InvolvedEntityIds { get; set; } = [];

    public WorldEventTriggerType TriggerType { get; set; } = WorldEventTriggerType.Scheduled;
    public int? IntervalDays { get; set; }
    public int? TargetDay { get; set; }
    public WorldEventCondition? Condition { get; set; }

    public List<WorldEventEffect> Effects { get; set; } = [];

    public WorldEventStatus Status { get; set; } = WorldEventStatus.Pending;
    public int? LastTriggeredDay { get; set; }
    public int? TriggeredOnDay { get; set; }

    public string? PreventedByEntityId { get; set; }
    public WorldEventCondition? PreventionCondition { get; set; }

    public int DayCreated { get; set; }
    public int LastUpdatedDay { get; set; }
    public bool IsPlayerVisible { get; set; }
    public string? DmNotes { get; set; }
    public string? CampaignName { get; set; }
    public bool IsArchived { get; set; }
}

public enum WorldEventTriggerType
{
    TimeBased,
    Scheduled,
    Conditional
}

public enum WorldEventStatus
{
    Pending,
    Triggered,
    Prevented,
    Resolved
}

public record WorldEventEffect(
    WorldEventEffectKind Kind,
    string? TargetFactionId = null,
    string? TargetCharacterId = null,
    FactionStance? NewStance = null,
    int? InfluenceDelta = null,
    string? RumorSubject = null,
    string? Text = null,
    string? LocationId = null
);

public enum WorldEventEffectKind
{
    RumorCreate,
    FactionStateChange,
    EventOccurred,
    KnowledgeUpdate
}

public record WorldEventCondition(
    WorldEventConditionKind Kind,
    string? TargetEntityId = null,
    double? NumericThreshold = null,
    string? EnumValue = null,
    List<WorldEventCondition>? AllOf = null
);

public enum WorldEventConditionKind
{
    FactionInfluenceAtLeast,
    FactionInfluenceAtMost,
    FactionStanceToward,
    PlotThreadStateIs,
    PlotThreadTensionAtLeast,
    QuestStateIs,
    DaysSinceDayElapsed
}
