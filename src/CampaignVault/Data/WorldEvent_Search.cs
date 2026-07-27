using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class WorldEvent_Search : AbstractIndexCreationTask<WorldEvent>
{
    public WorldEvent_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = events => from e in events
            select new
            {
                e.Title,
                e.Description,
                e.DmNotes,
                e.Status,
                e.TriggerType,
                e.TargetDay,
                e.ActorId,
                e.CampaignName,
                e.InvolvedEntityIds,
                SemanticVector = CreateVector(e.SemanticVector)
            };

        Index(x => x.Title, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
        Index(x => x.DmNotes, FieldIndexing.Search);
        Index(x => x.Status, FieldIndexing.Exact);
        Index(x => x.TriggerType, FieldIndexing.Exact);
        Index(x => x.ActorId, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
        Index(x => x.InvolvedEntityIds, FieldIndexing.Exact);
    }
}
