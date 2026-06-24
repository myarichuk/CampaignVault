using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public static class CampaignPostureBuilder
{
    public const string SharedCanonNote =
        "Entities with no CampaignName (e.g. chars/bob-the-assassin) are shared canon and appear in every campaign.";

    public static async Task<CampaignPosture> BuildAsync(
        IAsyncDocumentSession session,
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        string slug,
        bool isNewCampaign,
        CancellationToken cancellationToken = default)
    {
        var campaign = await session.LoadAsync<Campaign>(keys.Meta(slug), cancellationToken)
                       ?? new Campaign
                       {
                           Id = keys.Meta(slug),
                           Name = slug,
                           DisplayName = slug,
                       };

        var party = await session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(c => c.CampaignName == slug && (c.IsPc || c.IsPartyCompanion))
            .ToListAsync(cancellationToken);

        var pcs = party
            .Where(c => c.IsPc)
            .Select(c => new PartyMemberSummary(c.Id, c.Name, true))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var companions = party
            .Where(c => c.IsPartyCompanion)
            .Select(c => new PartyMemberSummary(c.Id, c.Name, false))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recentEvents = await repository.QueryEventsAsync(session, null, null, 1, slug);
        var lastSessionSummary = recentEvents.FirstOrDefault()?.Summary;

        var entryHint = ResolveEntryHint(isNewCampaign, pcs.Count, companions.Count);

        return new CampaignPosture(
            slug,
            string.IsNullOrWhiteSpace(campaign.DisplayName) ? slug : campaign.DisplayName,
            campaign.System,
            campaign.IsSystemLocked,
            pcs,
            companions,
            lastSessionSummary,
            entryHint,
            SharedCanonNote);
    }

    private static CampaignEntryHint ResolveEntryHint(bool isNewCampaign, int pcCount, int companionCount)
    {
        if (isNewCampaign)
        {
            return CampaignEntryHint.NewCampaign;
        }

        if (pcCount == 0)
        {
            return CampaignEntryHint.AddPc;
        }

        if (companionCount == 0)
        {
            return CampaignEntryHint.AddCompanion;
        }

        return CampaignEntryHint.Resume;
    }
}