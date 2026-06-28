using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Location_Search : AbstractIndexCreationTask<Location>
{
    public Location_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = locations => from l in locations
            select new
            {
                l.Name,
                l.Description,
                l.Type,
                l.ParentLocationId,
                CampaignName = l.CampaignName
,
                SemanticVector = CreateVector(l.SemanticVector)
            };

        
        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
    }
}
