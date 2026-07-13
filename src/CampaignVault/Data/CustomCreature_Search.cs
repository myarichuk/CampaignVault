using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class CustomCreature_Search : AbstractIndexCreationTask<CustomCreature>
{
    public CustomCreature_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = creatures => from c in creatures
            select new
            {
                c.Name,
                c.Description,
                c.System,
                c.CampaignName,
                c.Level,
                c.ChallengeRating,
                SemanticVector = CreateVector(c.SemanticVector)
            };

        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Description, FieldIndexing.Search);
        Index(x => x.System, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
    }
}
