using System;

namespace CampaignVault.Authoring.Vault.Sync;

public enum VaultConnectionState
{
    Unknown,
    Online,
    Offline,
    Error
}

public sealed record VaultConnectionStatus(
    VaultConnectionState State,
    string? Message = null,
    DateTimeOffset? LastChecked = null);