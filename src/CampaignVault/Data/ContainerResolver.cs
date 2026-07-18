using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Validates and queries item-as-container nesting (cycle-safety, depth limit, capacity).
/// Reused by ItemTransferHandler (validation) and scene assembly (surfacing container contents).
/// </summary>
public static class ContainerResolver
{
    public const int MaxNestingDepth = 8;

    /// <summary>
    /// Validates that moving <paramref name="movingItem"/> into <paramref name="destination"/> (an Item
    /// acting as a container) is safe: no cycle, no excessive nesting depth, and no capacity overflow.
    /// Returns null when valid, or a human-readable error message otherwise.
    /// </summary>
    public static async Task<string?> ValidateNestingAsync(
        IAsyncDocumentSession session,
        Item movingItem,
        Item destination,
        CancellationToken ct = default)
    {
        if (destination.Id.Equals(movingItem.Id, StringComparison.OrdinalIgnoreCase))
        {
            return $"Cannot move '{movingItem.Id}' into itself.";
        }

        var currentId = destination.HolderId;
        var depth = 0;

        while (!string.IsNullOrEmpty(currentId) && currentId.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
        {
            if (currentId.Equals(movingItem.Id, StringComparison.OrdinalIgnoreCase))
            {
                return $"Cannot move '{movingItem.Id}' into '{destination.Id}': would create a container cycle " +
                       $"('{movingItem.Id}' already (indirectly) contains '{destination.Id}').";
            }

            depth++;
            if (depth > MaxNestingDepth)
            {
                return $"Cannot move '{movingItem.Id}' into '{destination.Id}': container nesting exceeds depth limit of {MaxNestingDepth}.";
            }

            var parent = await session.LoadAsync<Item>(currentId, ct);
            if (parent == null) break;
            currentId = parent.HolderId;
        }

        if (destination.Capacity.HasValue)
        {
            var load = await GetContainerLoadAsync(session, destination.Id, ct);
            var addedQuantity = Math.Max(movingItem.Quantity, 1);
            if (load + addedQuantity > destination.Capacity.Value)
            {
                var unit = string.IsNullOrWhiteSpace(destination.CapacityUnit) ? "" : $" {destination.CapacityUnit}";
                return $"Cannot move '{movingItem.Id}' into '{destination.Id}': capacity {destination.Capacity.Value}{unit} " +
                       $"would be exceeded ({load} used, +{addedQuantity}).";
            }
        }

        return null;
    }

    /// <summary>Sum of Quantity across items directly held by <paramref name="containerId"/>.</summary>
    public static async Task<int> GetContainerLoadAsync(
        IAsyncDocumentSession session, string containerId, CancellationToken ct = default)
    {
        var contents = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereEquals(x => x.HolderId, containerId)
            .Take(256)
            .ToListAsync(ct);

        return contents.Sum(i => Math.Max(i.Quantity, 1));
    }

    /// <summary>Depth-capped recursive contents of a container (everything nested inside, however deep).</summary>
    public static async Task<List<Item>> GetRecursiveContentsAsync(
        IAsyncDocumentSession session, string containerId, CancellationToken ct = default)
    {
        var result = new List<Item>();
        await CollectAsync(session, containerId, result, 0, ct);
        return result;
    }

    /// <summary>Recursive contents as ContainedItemSummary with depth tracking and archived filtering.</summary>
    public static async Task<List<ContainedItemSummary>> GetRecursiveContentsSummariesAsync(
        IAsyncDocumentSession session, string containerId, int maxDepth = 3, CancellationToken ct = default)
    {
        var result = new List<ContainedItemSummary>();
        await CollectSummariesAsync(session, containerId, result, 0, maxDepth, ct);
        return result;
    }

    private static async Task CollectAsync(
        IAsyncDocumentSession session, string holderId, List<Item> result, int depth, CancellationToken ct)
    {
        if (depth >= MaxNestingDepth) return;

        var direct = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereEquals(x => x.HolderId, holderId)
            .Take(256)
            .ToListAsync(ct);

        foreach (var item in direct)
        {
            result.Add(item);
            await CollectAsync(session, item.Id, result, depth + 1, ct);
        }
    }

    private static async Task CollectSummariesAsync(
        IAsyncDocumentSession session, string holderId, List<ContainedItemSummary> result, int depth, int maxDepth, CancellationToken ct)
    {
        if (depth >= maxDepth) return;

        var direct = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
            .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
            .WhereEquals(x => x.HolderId, holderId)
            .Where(x => !x.IsArchived)
            .Take(256)
            .ToListAsync(ct);

        foreach (var item in direct)
        {
            var nested = new List<ContainedItemSummary>();
            await CollectSummariesAsync(session, item.Id, nested, depth + 1, maxDepth, ct);

            result.Add(new ContainedItemSummary(
                item.Id,
                item.Name,
                Math.Max(item.Quantity, 1),
                depth,
                nested.Count > 0 ? nested : null
            ));
        }
    }
}
