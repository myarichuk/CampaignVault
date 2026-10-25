namespace CampaignVault.Models;

/// <summary>
/// Wire projection of <see cref="Faction"/> for get_entity: the narrative and political facts, without the
/// document's bookkeeping (embedding hash, campaign name, archive flag, last-updated stamp).
/// </summary>
public record FactionDetailView(
    string Id,
    string Name,
    string? Description,
    FactionType FactionType,
    string? ControllingTerritory,
    List<string>? TerritoryLocationIds,
    List<string>? KnownLeaderIds,
    int InfluenceLevel,
    Dictionary<string, FactionStance>? StanceToward,
    Dictionary<string, float>? EconomicDemand,
    Dictionary<string, string>? Metadata,
    List<PlotThreadMinimal>? AssociatedPlotThreads)
{
    public static FactionDetailView From(Faction f, List<PlotThreadMinimal> threads) => new(
        f.Id,
        f.Name,
        f.Description,
        f.FactionType,
        f.ControllingTerritory,
        f.TerritoryLocationIds is { Count: > 0 } t ? t : null,
        f.KnownLeaderIds is { Count: > 0 } l ? l : null,
        f.InfluenceLevel,
        f.StanceToward is { Count: > 0 } s ? s : null,
        f.EconomicDemand is { Count: > 0 } e ? e : null,
        f.Metadata is { Count: > 0 } m ? m : null,
        threads.Count > 0 ? threads : null);
}
