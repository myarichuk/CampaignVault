using System;
using System.Collections.Generic;

namespace CampaignVault.Models;

public class Quest
{
    public string Id { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? GiverId { get; set; }
    public List<QuestObjective> Objectives { get; set; } = [];
    public QuestState OverallState { get; set; } = QuestState.Open;
    public string? Category { get; set; }
    public QuestUrgency Urgency { get; set; } = QuestUrgency.Normal;
    public List<string> RelatedLocationIds { get; set; } = [];
    public List<string> RelatedFactionIds { get; set; } = [];
    public string? DmNotes { get; set; }
    public List<string>? VisibleToCharacterIds { get; set; }
    public int? DeadlineDay { get; set; }
    public int LastUpdatedDay { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// </summary>
    public string? CampaignName { get; set; }
}

public record QuestObjective(
    string Description,
    QuestState State = QuestState.Open,
    string? RewardHint = null,
    List<string>? InvolvedIds = null,
    int? DayStarted = null,
    int? DayCompleted = null,
    int? DeadlineDay = null
);

public enum QuestState 
{ 
    Open, 
    InProgress, 
    Complete, 
    Failed, 
    Skipped 
}

public enum QuestUrgency 
{ 
    Low, 
    Normal, 
    Urgent, 
    Critical 
}
