// src/CampaignVault.Authoring/Models/UnifiedEntity.cs
using CommunityToolkit.Mvvm.ComponentModel;

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

public partial class UnifiedEntity : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _entityType = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalculatedState))]
    private string? _localHash;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalculatedState))]
    private string? _remoteHash;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalculatedState))]
    private string? _lastSyncedHash;

    [ObservableProperty]
    private string? _relativePath;

    [ObservableProperty]
    private string? _remoteMarkdown;

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
