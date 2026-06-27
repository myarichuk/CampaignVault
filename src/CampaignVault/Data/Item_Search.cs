using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

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
                i.Tags,
                CampaignName = i.CampaignName
,
                SemanticVector = CreateVector(i.SemanticVector)
            };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
    }
}
