namespace CampaignVault.Models;

public enum InitiativeDriver
{
    Relational,
    Memory,
    Need,
    Disposition
}

public record InitiativeCandidate(
    string Key,
    string NpcId,
    InitiativeDriver Driver,
    MemoryUrgency Urgency,
    string FramingPrompt,
    double Weight)
{
    public InitiativeCandidate() : this(default!, default!, default!, default!, default!, default!) { }
}

public record TensionBreakdown(
    float NeedStress,
    float MemoryStress,
    float RelationalStress,
    float DispositionStress)
{
    public TensionBreakdown() : this(default!, default!, default!, default!) { }
}

public record InitiativeSurfacedState(
    int SurfacedDay,
    string SurfacedViaTool,
    bool Consumed = true)
{
    public InitiativeSurfacedState() : this(default!, default!) { }
}

/// <summary>
/// Advisory-only "whose move is it" hint for RP scenes — never a hard gate, unlike combat's
/// ActiveTurnId/NotYourTurn. Holder is "player" or "npc"; Reason mirrors the top initiative
/// candidate's FramingPrompt when Holder is "npc".
/// </summary>
public record TurnIntentSignal(string Holder, string? Reason, MemoryUrgency? Confidence)
{
    public TurnIntentSignal() : this(default!, default, default) { }
}

public record NpcInitiativeEnrichment(
    double BehavioralTension,
    TensionBreakdown? TensionComponents,
    IReadOnlyList<InitiativeCandidate> ActiveInitiatives,
    IReadOnlyList<MemoryNode> RelevantMemories)
{
    public NpcInitiativeEnrichment() : this(default!, default!, default!, default!) { }

    /// <summary>Null means "open turn" — the player can always act. Advisory only.</summary>
    public TurnIntentSignal? TurnIntent { get; init; }
}