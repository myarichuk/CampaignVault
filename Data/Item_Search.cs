using Raven.Client.Documents.Indexes;
using CampaignVault.Models;

namespace CampaignVault.Data;

public class Item_Search : AbstractIndexCreationTask<Item>
{
    public Item_Search()
    {
        Map = items => from i in items
                        select new
                        {
                            i.Name,
                            i.Description,
                            i.HolderId,
                            i.Tags
                        };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
    }
}
