namespace CampaignVault.Models;

public class SessionLog : ICampaignScopedEntity
{
    public string Id { get; set; } = null!;
    public string? CampaignName { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public float[]? SemanticVector { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EmbeddingTextHash { get; set; }
    public List<SessionRecord> Sessions { get; set; } = [];

    public string BuildEmbeddingText() => $"Campaign {CampaignName}: {Sessions.Count} sessions";


    public class SessionRecord
    {
        public int Number { get; set; }
        public string? Title { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
        public int InWorldStartDay { get; set; }
        public string? InWorldStartTimeOfDay { get; set; }
        public int? InWorldEndDay { get; set; }
        public string? InWorldEndTimeOfDay { get; set; }
        public string? RecapText { get; set; }
        public List<string> KeyEventIds { get; set; } = [];
        public bool IsOpen { get; set; }
    }
}
