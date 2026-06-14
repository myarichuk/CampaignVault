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

public record NpcInitiativeEnrichment(
    double BehavioralTension,
    TensionBreakdown? TensionComponents,
    IReadOnlyList<InitiativeCandidate> ActiveInitiatives,
    IReadOnlyList<MemoryNode> RelevantMemories)
{
    public NpcInitiativeEnrichment() : this(default!, default!, default!, default!) { }
}