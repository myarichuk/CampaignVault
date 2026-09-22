namespace CampaignVault.Models;

/// <summary>
/// Minimal plot thread summary embedded in entity detail responses.
/// Payload restricted to: id, title, state, tensionLevel only.
/// </summary>
public record PlotThreadMinimal(
    string Id,
    string Title,
    PlotThreadState State,
    int TensionLevel);
