using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Rumor_Search : AbstractIndexCreationTask<Rumor>
{
    public Rumor_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = rumors => from r in rumors
            select new
            {
                r.Subject,
                r.CurrentText,
                r.RegionLocationId,
                r.State,
                CampaignName = r.CampaignName
,
                SemanticVector = CreateVector(r.SemanticVector)
            };

        
        Index(x => x.Subject, FieldIndexing.Search);
        Index(x => x.CurrentText, FieldIndexing.Search);
    }
}
