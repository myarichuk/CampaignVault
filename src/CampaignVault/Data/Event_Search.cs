using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Event_Search : AbstractIndexCreationTask<Event>
{
    public Event_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = events => from e in events
            select new
            {
                e.Summary,
                Category = e.Category,
                e.Timestamp,
                e.DayLogged,
                CampaignName = e.CampaignName,
                e.LocationId,
                e.RelatedLocationIds,
                e.Involved,
                e.Importance,
                SemanticVector = CreateVector(e.SemanticVector)
            };


        Index(x => x.Summary, FieldIndexing.Search);
        Index(x => x.CampaignName, FieldIndexing.Exact);
        Index(x => x.DayLogged, FieldIndexing.Exact);
        Index(x => x.LocationId, FieldIndexing.Exact);
        Index(x => x.RelatedLocationIds, FieldIndexing.Exact);
        Index(x => x.Involved, FieldIndexing.Exact);
    }
}
