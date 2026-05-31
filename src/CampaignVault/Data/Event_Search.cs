using Raven.Client.Documents.Indexes;
using CampaignVault.Models;

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
                            e.Timestamp
                        };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Summary, FieldIndexing.Search);
    }
}
