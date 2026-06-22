using CampaignVault.Models;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Character_Search : AbstractIndexCreationTask<Character>
{
    public Character_Search()
    {
        Map = characters => from c in characters
            select new
            {
                c.Name,
                c.Notes,
                c.ClassLevel,
                Locations = c.Schedule == null
                    ? new string[0]
                    : new[] { c.Schedule.DefaultLocationId }.Concat(c.Schedule.Routines != null
                        ? c.Schedule.Routines.Select(r => r.LocationId)
                        : new string[0]),
                // Live simulation state (CurrentLocationId / CurrentActivity) — enables efficient GetScene queries
                // without client-side scans over large character collections (addresses review issues #3 and #8).
                CurrentLocationId = c.CurrentLocationId,
                CurrentActivity = c.CurrentActivity,
                CampaignName = c.CampaignName,
                c.KeepAlive,
                c.MaxHp,
                HasSchedule = c.Schedule != null
            };

        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;
        Index(x => x.Name, FieldIndexing.Search);
        Index(x => x.Notes, FieldIndexing.Search);
        Index("CurrentLocationId", FieldIndexing.Exact);
    }
}