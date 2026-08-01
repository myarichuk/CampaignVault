namespace CampaignVault.Models;

/// <summary>
/// Tracks delivered guidance hints per campaign to enforce once-per-campaign delivery.
/// </summary>
public class GuidanceLedger
{
    public string Id { get; set; } = null!;
    public string CampaignName { get; set; } = null!;

    /// <summary>Maps GuidanceHint.Key to delivery metadata.</summary>
    public Dictionary<string, GuidanceDelivery> Delivered { get; set; } = [];

    /// <summary>Cumulative tokens spent on delivered hints.</summary>
    public int TokensDeliveredTotal { get; set; }
}

/// <summary>
/// Records when a hint was delivered to enforce repeat-after-days logic.
/// </summary>
public record GuidanceDelivery(int Day, DateTime AtUtc, string ToolName);
