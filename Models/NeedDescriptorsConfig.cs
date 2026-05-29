namespace CampaignVault.Models;

/// <summary>
/// Strongly-typed document for globally defined need descriptors.
/// Stored at the well-known ID "config/need-descriptors".
/// Using a real POCO (instead of raw Dictionary) ensures RavenDB can
/// serialize/deserialize it cleanly with its metadata envelope.
/// </summary>
public class NeedDescriptorsConfig
{
    public string Id { get; set; } = "config/need-descriptors";

    /// <summary>
    /// Case-insensitive mapping from need name (e.g. "wanderlust") to human-readable description.
    /// </summary>
    public Dictionary<string, string> Descriptors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
