using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

/// <summary>
/// Full-text search index for Quests.
/// Enables efficient queries by title, category, related locations/factions, giver, and urgency.
/// Used by QuestStalenessRule, get_world_state views, and get_scene location-overlap queries.
/// StandardAnalyzer on Title ensures "clear the cellar" resolves to "Clear the Cellar Rats".
/// </summary>
public class Quest_Search : AbstractIndexCreationTask<Quest>
{
    public Quest_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = quests => from q in quests
            select new
            {
                q.Title,
                q.Category,
                q.GiverId,
                q.OverallState,
                q.Urgency,
                q.CampaignName,
                q.DeadlineDay,
                q.IsArchived,
                RelatedLocationIds = q.RelatedLocationIds,
                RelatedFactionIds = q.RelatedFactionIds,
                VisibleToCharacterIds = q.VisibleToCharacterIds,
                LastUpdatedDay = q.LastUpdatedDay,
                // Flatten objectives for full-text search across descriptions
                ObjectiveDescriptions = q.Objectives != null ? q.Objectives.Select(o => o.Description) : new string[0]
,
                SemanticVector = CreateVector(q.SemanticVector)
            };

        
        Index(x => x.Title, FieldIndexing.Search);
        Index(x => x.Category, FieldIndexing.Search);
        Index("ObjectiveDescriptions", FieldIndexing.Search);
        Index(x => x.GiverId, FieldIndexing.Exact);
        Index(x => x.OverallState, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
        Index(x => x.DeadlineDay, FieldIndexing.Exact);
        Index(x => x.IsArchived, FieldIndexing.Exact);
        Index(x => x.RelatedLocationIds, FieldIndexing.Exact);
        Index(x => x.RelatedFactionIds, FieldIndexing.Exact);
        Index(x => x.VisibleToCharacterIds, FieldIndexing.Exact);
    }
}
