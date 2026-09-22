namespace CampaignVault.Models;

/// <summary>
/// Relative distance bands for zone/positioning (e.g. "the drunk is five paces away").
/// Distinct from <see cref="EngagementRelation"/> pairwise anchors.
/// </summary>
public static class SpatialDistanceBand
{
    public const string Touch = "Touch";
    public const string Close = "Close";
    public const string Near = "Near";
    public const string Far = "Far";
    public const string Distant = "Distant";
}