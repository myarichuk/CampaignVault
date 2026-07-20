using CampaignVault.Data.Initiative;
using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Resolves the set of items held/equipped by an entity, merging the pre-loaded/tracked
/// ChangeContext.Items with a session query (mirrors WeaponParameterResolver's held-weapon lookup).
/// Tracked entries win over stale query results for the same ID.
/// </summary>
internal static class ItemHolderQueryHelper
{
    public static async Task<List<Item>> GetHeldItemsAsync(
        ChangeContext context, string holderId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);

        if (context.Session != null && !string.IsNullOrWhiteSpace(holderId))
        {
            var held = await InitiativeQueryHelper.QueryItemsHeldByAsync(context.Session, holderId, ct: ct);
            foreach (var i in held) result[i.Id] = i;
        }

        foreach (var i in context.Items.Values)
        {
            if (string.Equals(i.HolderId, holderId, StringComparison.OrdinalIgnoreCase))
            {
                result[i.Id] = i;
            }
        }

        return result.Values.ToList();
    }

    public static async Task<List<Item>> GetEquippedItemsAsync(
        ChangeContext context, string holderId, string? excludeItemId = null, CancellationToken ct = default)
    {
        var held = await GetHeldItemsAsync(context, holderId, ct);
        return held
            .Where(i => i.IsEquipped)
            .Where(i => excludeItemId == null || !i.Id.Equals(excludeItemId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
