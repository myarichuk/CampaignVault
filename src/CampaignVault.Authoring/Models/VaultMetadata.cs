// src/CampaignVault.Authoring/Models/VaultMetadata.cs
namespace CampaignVault.Authoring.Models;

public class VaultMetadata
{
    public string CampaignName { get; set; } = string.Empty;
    public string? RemoteHost { get; set; }
}
