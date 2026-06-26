namespace CampaignVault.Authoring.Vault.Sync;

public enum VaultSyncState
{
    Absent,
    LocalOnly,
    RemoteOnly,
    Synced,
    BehindVault,
    AheadOfVault,
    Conflict,
    DeletedLocally,
    DeletedRemotely,
    Invalid
}