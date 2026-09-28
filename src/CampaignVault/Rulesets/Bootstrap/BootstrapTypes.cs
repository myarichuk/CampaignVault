using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets.Bootstrap;

// Bootstrap contracts (BootstrapContext, IBootstrapStep, pipelines) live in the
// PluginSdk so out-of-tree rulesets can derive stats. They stay Raven-free: steps
// that need worn gear read it through IBootstrapEquipmentAccess, adapted here.

/// <summary>Session-backed <see cref="IBootstrapEquipmentAccess"/> for host-run pipelines.</summary>
public sealed class SessionEquipmentAccess(IAsyncDocumentSession session) : IBootstrapEquipmentAccess
{
    public async Task<IReadOnlyList<Item>> GetEquippedItemsAsync(string characterId, CancellationToken ct = default)
    {
        var held = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereEquals(x => x.HolderId, characterId)
            .Take(50)
            .ToListAsync(ct);

        return [.. held.Where(i => i.IsEquipped)];
    }
}
