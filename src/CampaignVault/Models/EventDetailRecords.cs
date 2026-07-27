namespace CampaignVault.Models;

// Named shapes for the entries written into Event.Details (a persisted Dictionary<string, object>).
// Newtonsoft/RavenDB tags any boxed non-object runtime type with a "$type" discriminator so the
// value round-trips correctly. Anonymous types get a compiler-generated name
// (<>f__AnonymousTypeN) whose numbering is not stable across rebuilds, so a document written by
// one build can fail to deserialize after a later, unrelated rebuild renumbers the type. Named
// records keep a stable full type name instead.

public record ItemTransferDetail(string? ItemId, string? ToHolderId);

public record DamageDealtDetail(string? CharacterId, int Delta);

public record StatusAppliedDetail(string? CharacterId, string? StatusName, string? Category = null);

public record ResourceSpentDetail(string? CharacterId, string? Pool, int Delta);

public record RelationshipChangeDetail(string? CharacterId, string? TargetId, int Delta);

public record LocationVisitedDetail(string? CharacterId, string? Location, string? PoiName);

public record NeedChangedDetail(string? CharacterId, string? Need, float Delta);

public record QuestProgressedDetail(string? QuestId, QuestState NewState);

public record PlotThreadFactDetail(string? PlotThreadId, string? ClueId);

public record RulesetActionFactDetail(string? CharacterId, string? ActionType, string? ActionName);
