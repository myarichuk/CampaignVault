namespace CampaignVault.Models;

public class Character
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string? ClassLevel { get; set; }
    
    public int CurrentHp { get; set; }
    
    public int MaxHp { get; set; }
    

    
    public string? Notes { get; set; }
    
    /// <summary>
    /// If true, this character is protected from TransientEvictionRule even if Schedule == null.
    /// Use for player characters (PCs) and major named NPCs without fixed routines.
    /// </summary>
    public bool KeepAlive { get; set; } = false;
    
    public Schedule? Schedule { get; set; }

    /// <summary>
    /// Populated by ScheduleEvaluationRule and agency rules during AdvanceWorld.
    /// Represents where the NPC actually is and what they are doing right now.
    /// Used to make GetScene return living, time-aware results instead of static schedule data.
    /// </summary>
    public string? CurrentLocationId { get; set; }
    public string? CurrentActivity { get; set; }

    public PsychologyProfile Psychology { get; set; } = new();
    
    public SocialProfile Social { get; set; } = new();
    
    public NeedsProfile Needs { get; set; } = new();
    
    public SystemExtension SystemStats { get; set; } = new();
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class PsychologyProfile
{
    /// <summary>
    /// Open-ended knowledge graph. Maps entities/topics to what the NPC knows about them.
    /// Replaces the old 'Knows' list.
    /// Example: "Rusty Nail Tavern" -> "Owned by Bram. Serves watered-down ale."
    /// </summary>
    public Dictionary<string, string> KnowledgeGraph { get; set; } = [];
    public List<string> Wants { get; set; } = [];                    
    public List<string> Fears { get; set; } = [];                    
    public string? CurrentMood { get; set; }                         
}

public class SocialProfile
{
    public Dictionary<string, int> Relationships { get; set; } = []; 
}

public class NeedsProfile
{
    public Dictionary<string, float> ActiveNeeds { get; set; } = new()
    {
        ["hunger"] = 25f,
        ["thirst"] = 20f,
        ["tiredness"] = 15f,
        ["social_drive"] = 10f
    };

    /// <summary>
    /// Optional human/LLM-readable descriptions for the keys in ActiveNeeds.
    /// Example: "homesickness" -> "Longing for family and familiar places. High values cause distraction and poor sleep."
    /// </summary>
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];
}

[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$system", UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Dnd5eExtension), "dnd5e")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Pf2eExtension), "pf2e")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Fallout2d20Extension), "fallout2d20")]
public class SystemExtension
{
    // ── Cross-cutting stats present in virtually every TTRPG ──────────────────
    // These feed into NeedsAccumulationRule and ScheduleEvaluationRule
    // regardless of which ruleset plugin is active.

    /// <summary>
    /// Willpower / iron will — resisting fear, compulsion, mind control.
    /// D&amp;D Will Save bonus, PF2e Will DC anchor.
    /// </summary>
    public float Willpower { get; set; } = 75f;

    /// <summary>
    /// Morale — esprit de corps, fighting spirit, bravery under fire.
    /// Feeds into fear checks, NPC agency, and morale-based saving throws.
    /// </summary>
    public float Morale { get; set; } = 65f;

    /// <summary>
    /// Environmental temperature exposure (degrees C, 0 = comfortable).
    /// Used by NeedsAccumulationRule for hypothermia/heat-stroke effects.
    /// </summary>
    public float Temperature { get; set; } = 37f;

    /// <summary>
    /// Psychological stress / trauma accumulation (0–100).
    /// Direct analogue: CoC SAN loss, Alien RPG Stress, Delta Green Breaking Point.
    /// NeedsAccumulationRule can emit MoodChanges when this crests thresholds.
    /// </summary>
    public float Stress { get; set; } = 0f;

    /// <summary>
    /// Physical exhaustion level (0–100).
    /// D&amp;D 5e exhaustion track, PF2e Fatigued condition, survival systems.
    /// NeedsAccumulationRule writes here when tiredness exceeds critical thresholds.
    /// </summary>
    public float Fatigue { get; set; } = 0f;

    /// <summary>
    /// Spendable luck resource — hero points, bennies, fate points, inspiration.
    /// D&amp;D 5e Inspiration (0 or 1), PF2e Hero Points (0–3), Savage Worlds Bennies.
    /// </summary>
    public int LuckPoints { get; set; } = 0;

    /// <summary>
    /// Base movement speed in system-native units.
    /// D&amp;D/PF2e: feet (30). Fallout: null (uses range bands instead).
    /// Nullable — leave null for systems that do not use numeric movement.
    /// </summary>
    public float? Movement { get; set; }

    /// <summary>
    /// Open-ended custom narrative attributes (e.g. "corruption", "reputation", "fear", "honor", "debt_pressure").
    /// Also used by ruleset extensions for combat stats not covered by the named fields above.
    /// </summary>
    public Dictionary<string, float> Attributes { get; set; } = [];

    /// <summary>
    /// Structured status effects replacing the old flat <c>Character.Status: List&lt;string&gt;</c>.
    /// Each effect carries stat modifiers, expiration metadata, and a recovery hint authored by the LLM DM.
    /// See <see cref="StatusEffect"/> for the full design and tool-schema documentation.
    /// </summary>
    public List<StatusEffect> StatusEffects { get; set; } = [];
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
