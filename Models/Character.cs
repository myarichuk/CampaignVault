namespace CampaignVault.Models;

public class Character
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string? ClassLevel { get; set; }
    
    public int CurrentHp { get; set; }
    
    public int MaxHp { get; set; }
    
    public List<string> Status { get; set; } = [];
    
    /// <summary>
    /// LEGACY V3 field. Do not use for new code.
    /// All relationship tracking now lives in <see cref="Mind.Relationships"/>.
    /// </summary>
    [Obsolete("Use Mind.Relationships instead. This is a legacy V3 field and will be removed in a future version.")]
    public List<Relationship> Relationships { get; set; } = [];

    /// <summary>
    /// LEGACY V3 field. Do not use for new code.
    /// Knowledge tracking now lives in <see cref="Mind.Knows"/>.
    /// </summary>
    [Obsolete("Use Mind.Knows instead. This is a legacy V3 field and will be removed in a future version.")]
    public List<KnowledgeEdge> KnowledgeGraph { get; set; } = [];

    /// <summary>
    /// LEGACY V3 field. Do not use for new code.
    /// NPC needs/tiredness/etc. now live in <see cref="Mind.Needs"/>.
    /// </summary>
    [Obsolete("Use Mind.Needs instead. This is a legacy V3 field and will be removed in a future version.")]
    public Dictionary<string, int> Needs { get; set; } = [];
    
    public string? Notes { get; set; }
    
    public Schedule? Schedule { get; set; }

    public NpcMind Mind { get; set; } = new();
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class NpcMind
{
    public Dictionary<string, int> Relationships { get; set; } = []; 
    public List<string> Knows { get; set; } = [];                    
    public List<string> Wants { get; set; } = [];                    
    public List<string> Fears { get; set; } = [];                    
    public string? CurrentMood { get; set; }                         
    
    // Enhanced Needs
    public Dictionary<string, float> Needs { get; set; } = new()
    {
        ["hunger"] = 25f,
        ["thirst"] = 20f,
        ["tiredness"] = 15f,
        ["arousal"] = 10f
    };

    // Attributes
    public float Willpower { get; set; } = 75f;
    public float Temperature { get; set; } = 37f;
    public float Morale { get; set; } = 65f;

    public float LastSimulatedDay { get; set; }
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
    public string Condition { get; set; } = default!;
    
    public string LocationId { get; set; } = default!;
    
    public string Activity { get; set; } = default!;
    
    public double Probability { get; set; } = 1.0;
}

public class StateModifier
{
    public string Type { get; set; } = default!;
    
    public string Description { get; set; } = default!;
    
    public string? OverrideLocationId { get; set; }
    
    public string? OverrideActivity { get; set; }
    
    public int? ExpiryDay { get; set; }
}
