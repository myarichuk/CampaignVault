using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Event_Search : AbstractIndexCreationTask<Event>
{
    public Event_Search()
    {
        Map = events => from e in events
            select new
            {
                e.Summary,
                Category = e.Category,
                e.Timestamp,
                e.DayLogged,
                CampaignName = e.CampaignName
,
                SemanticVector = CreateVector(e.SemanticVector)
            };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Summary, FieldIndexing.Search);
        Index(x => x.CampaignName, FieldIndexing.Exact);
        Index(x => x.DayLogged, FieldIndexing.Exact);
    }
}
