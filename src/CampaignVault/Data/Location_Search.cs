using Raven.Client.Documents.Indexes;
using CampaignVault.Models;

namespace CampaignVault.Data;

public class Location_Search : AbstractIndexCreationTask<Location>
{
    public Location_Search()
    {
        Map = locations => from l in locations
                            select new
                            {
                                l.Name,
                                l.Description,
                                l.Type,
                                l.ParentLocationId
                            };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
    }
}
