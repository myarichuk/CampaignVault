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
    
    public Schedule? Schedule { get; set; }

    public NpcMind Mind { get; set; } = new();
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class NpcMind
{
    public Dictionary<string, int> Relationships { get; set; } = []; // TargetId -> Disposition (-100 to 100)
    public List<string> Knows { get; set; } = [];                    // Rumor/Lore IDs
    public List<string> Wants { get; set; } = [];                    // Short/long term goals
    public List<string> Fears { get; set; } = [];                    
    public string? CurrentMood { get; set; }                         
    public Dictionary<string, int> Needs { get; set; } = [];         // Hunger, fatigue, etc.
}

public record Relationship(string Target, string Description);

public record KnowledgeEdge(string TargetId, string Description);

public class Schedule
{
    public string DefaultLocationId { get; set; } = default!;
    
    public List<Routine> Routines { get; set; } = [];
    
    public List<StateModifier> ActiveModifiers { get; set; } = [];
}

public class Routine
{
    public string Condition { get; set; } = default!; // e.g. "Evening", "Sunday"
    
    public string LocationId { get; set; } = default!;
    
    public string Activity { get; set; } = default!;
    
    public double Probability { get; set; } = 1.0;
}

public class StateModifier
{
    public string Type { get; set; } = default!; // Fear, Weather, Faction, Injury, Quest, Relationship
    
    public string Description { get; set; } = default!;
    
    public string? OverrideLocationId { get; set; }
    
    public string? OverrideActivity { get; set; }
    
    public int? ExpiryDay { get; set; }
}
