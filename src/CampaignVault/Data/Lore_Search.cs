using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Lore_Search : AbstractIndexCreationTask<Lore>
{
    public Lore_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = lores => from lore in lores
            select new
            {
                lore.Title,
                lore.Content,
                lore.Tags,
                lore.Category,
                CampaignName = lore.CampaignName
,
                SemanticVector = CreateVector(lore.SemanticVector)
            };

        

        Index(x => x.Title, FieldIndexing.Search);
        Index(x => x.Content, FieldIndexing.Search);
    }
}
