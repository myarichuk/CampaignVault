using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class CustomFeat_Search : AbstractIndexCreationTask<CustomFeat>
{
    public CustomFeat_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = feats => from f in feats
            select new
            {
                f.Name,
                f.Description,
                f.System,
                f.CampaignName,
                SemanticVector = CreateVector(f.SemanticVector)
            };

        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
        Index(x => x.System, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
    }
}
