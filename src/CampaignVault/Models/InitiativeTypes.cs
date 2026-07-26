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
    public InitiativeCandidate() : this(null!, null!, default!, default!, null!, 0!) { }
}

public record TensionBreakdown(
    float NeedStress,
    float MemoryStress,
    float RelationalStress,
    float DispositionStress)
{
    public TensionBreakdown() : this(0!, 0!, 0!, 0!) { }
}

public record InitiativeSurfacedState(
    int SurfacedDay,
    string SurfacedViaTool,
    bool Consumed = true)
{
    public InitiativeSurfacedState() : this(0!, null!) { }
}

/// <summary>
/// Advisory-only "whose move is it" hint for RP scenes — never a hard gate, unlike combat's
/// ActiveTurnId/NotYourTurn. Holder is "player" or "npc"; Reason mirrors the top initiative
/// candidate's FramingPrompt when Holder is "npc".
/// </summary>
public record TurnIntentSignal(string Holder, string? Reason, MemoryUrgency? Confidence)
{
    public TurnIntentSignal() : this(null!, null, null) { }
}

public record NpcInitiativeEnrichment(
    double BehavioralTension,
    TensionBreakdown? TensionComponents,
    IReadOnlyList<InitiativeCandidate> ActiveInitiatives,
    IReadOnlyList<MemoryNode> RelevantMemories)
{
    public NpcInitiativeEnrichment() : this(0!, null!, null!, null!) { }

    /// <summary>Null means "open turn" — the player can always act. Advisory only.</summary>
    public TurnIntentSignal? TurnIntent { get; init; }
}