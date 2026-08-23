namespace CampaignVault.Models;

/// <summary>
/// Minimal plot thread summary embedded in entity detail responses.
/// Payload restricted to: id, title, state, tensionLevel only.
/// </summary>
public record PlotThreadMinimal(
    string Id,
    string Title,
    PlotThreadState State,
    int TensionLevel);

/// <summary>
/// GM/DM-eyes-only authored content. This is backstage material for your own use — judging pacing,
/// tension, and what an NPC privately wants or knows — not something the player character already
/// knows. Never narrate it verbatim or treat it as spoken/observed fact. Only surface it once the
/// player actually discovers it through play (a clue, a conversation, a search, a document found
/// in-world, etc).
/// </summary>
public record GmOnly(string? Notes);

/// <summary>
/// Wire-facing projection of <see cref="Character"/> for response views (NpcContextView, PartyMemberView) —
/// drops pure engine bookkeeping the LLM never needs for narration: Schedule (internal simulation
/// routines — CurrentLocationId/CurrentActivity already surface the result), TagProvenance (event-id
/// provenance for tags/features, not narrative content), the five rest-recovery counters (gate HP/resource
/// pool math internally; their effects are already visible via CurrentHp/SystemStats), CampaignName, and
/// LastUpdated. Includes Psychology/Social/Needs/SystemStats — read those off this object rather than a
/// top-level copy, to avoid shipping them twice on the wire. Notes is fenced under GmOnly — see that
/// type's doc comment for how to treat it.
/// </summary>
public record CharacterDetailView(
    string Id,
    string Name,
    string? ClassLevel,
    int ExperiencePoints,
    int CurrentHp,
    int MaxHp,
    GmOnly GmOnly,
    List<string> DistinctiveFeatures,
    string? CurrentAppearance,
    List<string> VisualTags,
    bool KeepAlive,
    bool IsPc,
    bool IsPartyCompanion,
    string? CurrentLocationId,
    string? CurrentActivity,
    int? DepartedAtDay,
    string? DepartedFromLocationId,
    PsychologyProfile Psychology,
    SocialProfile Social,
    NeedsProfile Needs,
    SystemExtension SystemStats)
{
    public static CharacterDetailView From(Character c) => new(
        c.Id,
        c.Name,
        c.ClassLevel,
        c.ExperiencePoints,
        c.CurrentHp,
        c.MaxHp,
        new GmOnly(c.Notes),
        c.DistinctiveFeatures,
        c.CurrentAppearance,
        c.VisualTags,
        c.KeepAlive,
        c.IsPc,
        c.IsPartyCompanion,
        c.CurrentLocationId,
        c.CurrentActivity,
        c.DepartedAtDay,
        c.DepartedFromLocationId,
        c.Psychology,
        c.Social,
        c.Needs,
        c.SystemStats);
}

public class NpcContextView
{
    /// <summary>
    /// Character projection, including Psychology/Social/Needs/SystemStats — read those off
    /// this object rather than a top-level copy, to avoid shipping them twice on the wire.
    /// </summary>
    public CharacterDetailView Character { get; set; } = null!;
    public IEnumerable<EventSummaryView> RecentInteractions { get; set; } = [];
    public string? BehavioralSummary { get; set; }

    /// <summary>
    /// All known needs for this NPC with their current values. The needs system is intentionally open-ended.
    /// </summary>
    public Dictionary<string, float> KnownNeeds { get; set; } = [];

    /// <summary>
    /// Human/LLM-readable descriptions for the needs (seeded by world-builder or previous LLM actions).
    /// </summary>
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];

    public double BehavioralTension { get; set; }
    public TensionBreakdown? TensionComponents { get; set; }
    public List<InitiativeCandidate> ActiveInitiatives { get; set; } = [];
    public List<MemoryNode> RelevantMemories { get; set; } = [];
    public List<ItemSummaryView>? Equipped { get; set; }
    public List<ItemSummaryView>? Carried { get; set; }

    /// <summary>Advisory-only "whose move is it" hint for this NPC — never a hard gate.</summary>
    public TurnIntentSignal? TurnIntent { get; set; }

    /// <summary>Plot threads associated with this NPC (referenced via thread-level or clue-level involvedEntityIds).</summary>
    public List<PlotThreadMinimal> AssociatedPlotThreads { get; set; } = [];
}

/// <summary>
/// Lightweight view returned by GetNpcNeeds for discoverability.
/// </summary>
public class NpcNeedsView
{
    public string CharacterId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Dictionary<string, float> KnownNeeds { get; set; } = [];
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];
}

/// <summary>
/// Lightweight summary of an NPC for quick lookups (get_npc_summary).
/// Slices the heavier NpcContextView to essential roleplay data: name, behavior, needs, and gear.
/// </summary>
public class NpcSummaryView
{
    public string CharacterId { get; set; } = null!;
    public string Name { get; set; } = null!;

    /// <summary>Null in take_turn delta mode when appearance/visual tags/features weren't touched this
    /// turn — the client already has the last-known value from the last full reseed or a prior delta
    /// that did change it. Always populated outside take_turn (e.g. get_npc_summary) and in Full mode.</summary>
    public string? CurrentAppearance { get; set; }

    public string? CurrentActivity { get; set; }
    public string? CurrentMood { get; set; }

    /// <summary>Null in take_turn delta mode when neither mood nor activity changed this turn (skips
    /// regenerating a summary the client already has). Always populated outside take_turn / Full mode.</summary>
    public string? BehavioralSummary { get; set; }

    /// <summary>In take_turn delta mode, filtered to needs that moved >= 2 points this turn. Always the
    /// full known-needs set outside take_turn / Full mode.</summary>
    public Dictionary<string, float> KnownNeeds { get; set; } = [];

    /// <summary>Null in take_turn delta mode when this NPC's gear/stats weren't touched this turn.</summary>
    public List<ItemSummaryView>? Equipped { get; set; }
    public List<ItemSummaryView>? Carried { get; set; }

    /// <summary>RP-advisory initiative/memory enrichment, present only for the up-to-2 NPCs selected this
    /// take_turn call (see MutationTools.SelectAndEnrichInitiativeAsync). Null for everyone else.</summary>
    public NpcInitiativeEnrichment? Initiative { get; set; }

    /// <summary>Set (take_turn only) when this NPC has a high-salience memory that exists but wasn't
    /// surfaced via Initiative this turn (NPC wasn't one of the up-to-2 selected) — a nudge to call
    /// get_entity/recall_history rather than assume nothing relevant is aging in the background.</summary>
    public string? MemoryHint { get; set; }
}
