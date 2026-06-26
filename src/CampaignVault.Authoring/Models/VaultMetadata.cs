using System;

namespace CampaignVault.Authoring.Models;

public class VaultMetadata
{
    public int SchemaVersion { get; set; } = 1;

    public string CampaignName { get; set; } = string.Empty;

    public string? Ruleset { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}