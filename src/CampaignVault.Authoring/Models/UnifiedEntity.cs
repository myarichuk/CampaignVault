// src/CampaignVault.Authoring/Models/UnifiedEntity.cs
namespace CampaignVault.Authoring.Models;

public enum SyncState
{
    Synced,
    LocalOnly,
    RemoteOnly,
    ModifiedLocally,
    ModifiedRemotely,
    Conflict
}

public class UnifiedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? LocalHash { get; set; }
    public string? RemoteHash { get; set; }
    public string? LastSyncedHash { get; set; }
    public string? RelativePath { get; set; }
    public string? RemoteMarkdown { get; set; }

    public SyncState CalculatedState
    {
        get
        {
            if (LocalHash == null && RemoteHash != null) return SyncState.RemoteOnly;
            if (LocalHash != null && RemoteHash == null) return SyncState.LocalOnly;
            if (LocalHash == RemoteHash) return SyncState.Synced;
            if (LocalHash != LastSyncedHash && RemoteHash == LastSyncedHash) return SyncState.ModifiedLocally;
            if (LocalHash == LastSyncedHash && RemoteHash != LastSyncedHash) return SyncState.ModifiedRemotely;
            return SyncState.Conflict;
        }
    }
}
