using System;

namespace CampaignVault.Authoring.Vault.Sync;

public sealed record VaultEntitySyncPlan(
    string EntityId,
    string EntityType,
    string? RelativePath,
    VaultSyncState State,
    string? LocalCanonicalHash = null,
    string? BaseCanonicalHash = null,
    string? RemoteCanonicalHash = null,
    string? ParseError = null);

public sealed record VaultSyncSummary(
    int SyncedCount,
    int AheadCount,
    int BehindCount,
    int ConflictCount,
    int LocalOnlyCount,
    int RemoteOnlyCount,
    int DeletedLocallyCount,
    int DeletedRemotelyCount,
    int InvalidCount,
    int AbsentCount,
    VaultConnectionStatus Connection,
    DateTimeOffset? LastFetchedAt = null,
    bool RemoteCacheCorrupt = false);