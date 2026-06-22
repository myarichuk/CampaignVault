using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

/// <summary>
/// Full-text search index for Factions.
/// Enables efficient queries by name, type, territory, stance, and influence level.
/// Used by FactionEcosystemRule, get_scene territory-overlap queries, and SuggestFactionsAsync.
/// StandardAnalyzer on Name ensures "iron league" resolves to "The Iron League".
/// </summary>
public class Faction_Search : AbstractIndexCreationTask<Faction>
{
    public Faction_Search()
    {
        Map = factions => from f in factions
            select new
            {
                f.Name,
                f.Description,
                f.FactionType,
                f.InfluenceLevel,
                f.CampaignName,
                f.ControllingTerritory,
                TerritoryLocationIds = f.TerritoryLocationIds,
                KnownLeaderIds = f.KnownLeaderIds
            };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
        Index(x => x.FactionType, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
        Index(x => x.ControllingTerritory, FieldIndexing.Exact);
    }
}