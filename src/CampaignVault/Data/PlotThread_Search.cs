using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class PlotThread_Search : AbstractIndexCreationTask<PlotThread>
{
    public PlotThread_Search()
    {
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        Vector("SemanticVector", f => f);
        Map = threads => from t in threads
            select new
            {
                t.Title,
                t.Summary,
                t.DmNotes,
                t.State,
                t.CampaignName,
                t.LastUpdatedDay,
                t.TensionLevel,
                t.DeadlineDay,
                ClueDescriptions = t.Clues != null ? t.Clues.Select(c => c.Description) : new string[0],
                SemanticVector = CreateVector(t.SemanticVector)
            };

        Index(x => x.Title, FieldIndexing.Search);
        Index(x => x.Summary, FieldIndexing.Search);
        Index(x => x.DmNotes, FieldIndexing.Search);
        Index("ClueDescriptions", FieldIndexing.Search);
        Index(x => x.State, FieldIndexing.Exact);
        Index(x => x.CampaignName, FieldIndexing.Exact);
    }
}
