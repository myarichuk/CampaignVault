using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class CustomSpell_Search : AbstractIndexCreationTask<CustomSpell>
{
    public CustomSpell_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = spells => from s in spells
            select new
            {
                s.Name,
                s.Description,
                s.System,
                s.CampaignName,
                s.Level,
                SemanticVector = CreateVector(s.SemanticVector)
            };

        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
        Index(x => x.System, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
    }
}
