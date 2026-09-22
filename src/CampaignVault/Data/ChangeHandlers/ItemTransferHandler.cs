using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ItemTransferHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemTransfer;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var transfer = (ItemTransfer)change;

        if (!ctx.Items.TryGetValue(transfer.ItemId, out var item))
        {
            item = await ctx.Session.LoadAsync<Item>(transfer.ItemId, ct);
            if (item == null)
            {
                var hints = await ctx.SuggestItemMatchAsync(transfer.ItemId);
                var msg = $"Item {transfer.ItemId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewItem(item);
        }

        // Verify destination exists (must be a character, location, or container item)
        var destinationExists = ctx.Characters.ContainsKey(transfer.ToHolderId)
            || ctx.Locations.ContainsKey(transfer.ToHolderId)
            || ctx.Items.ContainsKey(transfer.ToHolderId);

        var destinationItem = ctx.Items.GetValueOrDefault(transfer.ToHolderId);

        if (!destinationExists)
        {
            // Try loading from session if not in ctx
            try
            {
                if (transfer.ToHolderId.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
                {
                    destinationItem = await ctx.Session.LoadAsync<Item>(transfer.ToHolderId, ct);
                    destinationExists = destinationItem != null;
                }
                else
                {
                    var dest = await ctx.Session.LoadAsync<dynamic>(transfer.ToHolderId, ct);
                    destinationExists = dest != null;
                }
            }
            catch
            {
                destinationExists = false;
            }
        }

        if (!destinationExists)
        {
            return ChangeHandlerResult.Failure($"Destination {transfer.ToHolderId} does not exist. Item {transfer.ItemId} not transferred.");
        }

        if (destinationItem != null)
        {
            var nestingError = await ContainerResolver.ValidateNestingAsync(ctx.Session, item, destinationItem, ct);
            if (nestingError != null)
            {
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(nestingError);
            }
        }

        var previousHolderId = item.HolderId;
        var wasEquipped = item.IsEquipped;

        // Transfer the item
        item.HolderId = transfer.ToHolderId;
        item.LastUpdated = DateTime.UtcNow;

        // If transferring to a character or container, clear ambient-decay persistence
        // (no longer ambient at a location)
        if (transfer.ToHolderId.StartsWith("chars/", StringComparison.OrdinalIgnoreCase)
            || transfer.ToHolderId.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
        {
            item.Persistence = null;
        }

        // If the item was equipped and holder changed, unequip it and recompute AC/warmth for previous holder
        var autoUnequipped = wasEquipped && !string.IsNullOrEmpty(previousHolderId) && previousHolderId != transfer.ToHolderId;
        if (autoUnequipped)
        {
            item.IsEquipped = false;

            // Recompute AC/warmth for the previous holder if it's a character
            if (previousHolderId!.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Characters.TryGetValue(previousHolderId, out var previousHolder))
                {
                    await ArmorParameterResolver.ApplyAsync(previousHolder, ctx, ct);
                }
                else
                {
                    var prevChar = await ctx.Session.LoadAsync<Character>(previousHolderId, ct);
                    if (prevChar != null)
                    {
                        await ArmorParameterResolver.ApplyAsync(prevChar, ctx, ct);
                        ctx.RegisterNewCharacter(prevChar);
                    }
                }
            }
        }

        // Only report the side effect the caller didn't ask for — the transfer destination itself is
        // an echo of what it just specified.
        if (autoUnequipped)
        {
            ctx.RecordMessage($"Item {transfer.ItemId} was equipped and is auto-unequipped from {previousHolderId} by this transfer.");
        }

        return ChangeHandlerResult.Ok;
    }
}