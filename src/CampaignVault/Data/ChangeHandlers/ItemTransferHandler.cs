using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ItemTransferHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemTransfer;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var transfer = (ItemTransfer)change;

        if (!context.Items.TryGetValue(transfer.ItemId, out var item))
        {
            item = context.Session != null ? await context.Session.LoadAsync<Item>(transfer.ItemId, ct) : null;
            if (item == null)
            {
                var hints = await context.SuggestItemMatchAsync(transfer.ItemId);
                var msg = $"Item {transfer.ItemId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewItem(item);
        }

        item.HolderId = transfer.ToHolderId;
        item.LastUpdated = DateTime.UtcNow;
        context.RecordMessage($"Item {transfer.ItemId} moved to {transfer.ToHolderId}");

        return ChangeHandlerResult.Ok;
    }
}