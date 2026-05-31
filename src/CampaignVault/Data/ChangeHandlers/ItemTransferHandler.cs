using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ItemTransferHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemTransfer;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var transfer = (ItemTransfer)change;

        if (!context.Items.TryGetValue(transfer.ItemId, out var item))
        {
            // Item not preloaded — rare but possible if not referenced in pre-load scan
            context.RecordMessage($"WARNING: Item {transfer.ItemId} not found during ItemTransfer.");
            context.RecordFailure();
            return Task.FromResult(ChangeHandlerResult.Failure());
        }

        item.HolderId = transfer.ToHolderId;
        item.LastUpdated = DateTime.UtcNow;
        context.RecordMessage($"Item {transfer.ItemId} moved to {transfer.ToHolderId}");

        return Task.FromResult(ChangeHandlerResult.Ok);
    }
}