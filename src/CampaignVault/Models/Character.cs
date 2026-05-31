namespace CampaignVault.Models;

public class Character
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string? ClassLevel { get; set; }
    
    public int CurrentHp { get; set; }
    
    public int MaxHp { get; set; }
    
    public List<string> Status { get; set; } = [];
    
    public string? Notes { get; set; }
    
    public Schedule? Schedule { get; set; }

    /// <summary>
    /// Populated by ScheduleEvaluationRule and agency rules during AdvanceWorld.
    /// Represents where the NPC actually is and what they are doing right now.
    /// Used to make GetScene return living, time-aware results instead of static schedule data.
    /// </summary>
    public string? CurrentLocationId { get; set; }
    public string? CurrentActivity { get; set; }

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
        ["social_drive"] = 10f
    };

    /// <summary>
    /// Optional human/LLM-readable descriptions for the keys in Needs.
    /// Example: "homesickness" -> "Longing for family and familiar places. High values cause distraction and poor sleep."
    /// This makes the open needs system discoverable and self-documenting.
    /// </summary>
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];

    // Attributes (core three are promoted for convenience + special ranges; others go in the open dict)
    public float Willpower { get; set; } = 75f;
    public float Temperature { get; set; } = 37f;
    public float Morale { get; set; } = 65f;

    /// <summary>
    /// Open-ended custom narrative attributes (e.g. "corruption", "reputation", "fear", "honor", "debt_pressure").
    /// Any AttributeChange whose name is not one of the three core attributes lands here.
    /// This matches the open-vocabulary design already used for Needs.
    /// </summary>
    public Dictionary<string, float> Attributes { get; set; } = [];
}

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
