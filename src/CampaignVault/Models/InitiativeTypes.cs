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
    double Weight);

public record TensionBreakdown(
    float NeedStress,
    float MemoryStress,
    float RelationalStress,
    float DispositionStress);

public record InitiativeSurfacedState(
    int SurfacedDay,
    string SurfacedViaTool,
    bool Consumed = true);

public record NpcInitiativeEnrichment(
    double BehavioralTension,
    TensionBreakdown? TensionComponents,
    IReadOnlyList<InitiativeCandidate> ActiveInitiatives,
    IReadOnlyList<MemoryNode> RelevantMemories);