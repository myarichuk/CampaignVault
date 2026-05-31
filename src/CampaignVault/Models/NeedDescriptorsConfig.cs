namespace CampaignVault.Models;

/// <summary>
/// Strongly-typed document for campaign-specific need descriptors.
/// Document ID is now provided by CampaignDocumentKeys.NeedDescriptors(campaignName)
/// (e.g. "campaigns/{name}/config/need-descriptors").
/// </summary>
public class NeedDescriptorsConfig
{
    public string Id { get; set; } = default!;

    /// <summary>
    /// Case-insensitive mapping from need name (e.g. "wanderlust") to human-readable description.
    /// </summary>
    public Dictionary<string, string> Descriptors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
