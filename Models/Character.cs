namespace CampaignVault.Models;

public class Character
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string? ClassLevel { get; set; }
    
    public int CurrentHp { get; set; }
    
    public int MaxHp { get; set; }
    
    public List<string> Status { get; set; } = [];
    
    public List<Relationship> Relationships { get; set; } = [];

    public List<KnowledgeEdge> KnowledgeGraph { get; set; } = [];

    public Dictionary<string, int> Needs { get; set; } = [];
    
    public string? Notes { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public record Relationship(string Target, string Description);

public record KnowledgeEdge(string TargetId, string Description);
