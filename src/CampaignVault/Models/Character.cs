namespace CampaignVault.Models;

public class Character
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string? ClassLevel { get; set; }
    
    public int CurrentHp { get; set; }
    
    public int MaxHp { get; set; }
    

    public string? Notes { get; set; }
    
    public List<string> DistinctiveFeatures { get; set; } = [];
    
    public string? CurrentAppearance { get; set; }
    
    public List<string> VisualTags { get; set; } = [];
    
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

    /// <summary>
    /// Associates the entity with a specific campaign for multi-campaign isolation.
    /// Set automatically from current campaign context on create/upsert (via repo + handlers).
    /// (No legacy BC requirement per review feedback; always set for new data.)
    /// </summary>
    public string? CampaignName { get; set; }
}

public class PsychologyProfile
{
    /// <summary>
    /// Open-ended knowledge graph. Maps entities/topics to what the NPC knows about them.
    /// Replaces the old 'Knows' list and old string-based KnowledgeGraph.
    /// Example: "Rusty Nail Tavern" -> MemoryNode
    /// </summary>
    public Dictionary<string, MemoryNode> Memories { get; set; } = [];
    public List<string> Wants { get; set; } = [];
    public List<string> Fears { get; set; } = [];

    public string? CurrentMood { get; set; }

    // Phase 10: personality (merged — no separate PersonalityProfile)
    public List<string> Traits { get; set; } = [];
    public double Openness { get; set; } = 0.5;
    public double Resilience { get; set; } = 0.5;
}

public enum MemoryImportance
{
    Trivial,
    Important,
    Core
}

public enum MemorySource
{
    Witnessed,
    Heard,
    Told,
    Experienced,
    Trauma,
    Conditioned
}

public enum EmotionalValence
{
    Positive,
    Negative,
    Neutral,
    Traumatic
}

public enum MemoryUrgency
{
    Low,
    Normal,
    High,
    Urgent
}

public class MemoryNode
{
    public string Topic { get; set; } = default!;
    public string Details { get; set; } = default!;
    public int DayAcquired { get; set; } = 0;
    public MemoryImportance Importance { get; set; } = MemoryImportance.Important;

    public MemorySource Source { get; set; } = MemorySource.Told;
    public EmotionalValence Valence { get; set; } = EmotionalValence.Neutral;
    public double Salience { get; set; } = 0.5;
    public List<string> RelatedEntityIds { get; set; } = [];
    public string? TriggerCondition { get; set; }
    public MemoryUrgency Urgency { get; set; } = MemoryUrgency.Normal;

    /// <summary>
    /// Applies migration defaults for documents saved before Phase 10 enrichment fields existed.
    /// Legacy nodes deserialize with Salience=0 and enum zero-values; normalize once on touch.
    /// </summary>
    public void ApplyMigrationDefaultsIfNeeded()
    {
        if (Salience > 0)
        {
            return;
        }

        Source = MemorySource.Told;
        Valence = EmotionalValence.Neutral;
        Salience = 0.5;
        Urgency = MemoryUrgency.Normal;
    }
}
public class SocialProfile
{
    public Dictionary<string, int> Relationships { get; set; } = []; 
    public Dictionary<string, int> FactionReputations { get; set; } = [];
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

    public bool ActivityConflictActive { get; set; }
    public string? ActivityConflictNeed { get; set; }
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
    /// Multipliers for incoming damage types (e.g., "Fire" -> 0.5 for resistance).
    /// Used by EncounterResolver and ruleset-specific combat logic.
    /// </summary>
    public Dictionary<string, float> DamageModifiers { get; set; } = [];

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
