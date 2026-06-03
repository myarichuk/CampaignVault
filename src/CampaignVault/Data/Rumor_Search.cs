using Raven.Client.Documents.Indexes;
using CampaignVault.Models;

namespace CampaignVault.Data;

public class Rumor_Search : AbstractIndexCreationTask<Rumor>
{
    public Rumor_Search()
    {
        Map = rumors => from r in rumors
                        select new
                        {
                            r.Subject,
                            r.CurrentText,
                            r.RegionLocationId,
                            r.State,
                            CampaignName = r.CampaignName
                        };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Subject, FieldIndexing.Search);
        Index(x => x.CurrentText, FieldIndexing.Search);
    }
}
