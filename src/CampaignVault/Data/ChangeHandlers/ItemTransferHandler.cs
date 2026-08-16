using CampaignVault.Models;
using CampaignVault.Rulesets;

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
            item = await context.Session.LoadAsync<Item>(transfer.ItemId, ct);
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

        // Verify destination exists (must be a character, location, or container item)
        var destinationExists = context.Characters.ContainsKey(transfer.ToHolderId)
            || context.Locations.ContainsKey(transfer.ToHolderId)
            || context.Items.ContainsKey(transfer.ToHolderId);

        var destinationItem = context.Items.GetValueOrDefault(transfer.ToHolderId);

        if (!destinationExists)
        {
            // Try loading from session if not in context
            try
            {
                if (transfer.ToHolderId.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
                {
                    destinationItem = await context.Session.LoadAsync<Item>(transfer.ToHolderId, ct);
                    destinationExists = destinationItem != null;
                }
                else
                {
                    var dest = await context.Session.LoadAsync<dynamic>(transfer.ToHolderId, ct);
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
            var nestingError = await ContainerResolver.ValidateNestingAsync(context.Session, item, destinationItem, ct);
            if (nestingError != null)
            {
                context.RecordFailure();
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
                if (context.Characters.TryGetValue(previousHolderId, out var previousHolder))
                {
                    await ArmorParameterResolver.ApplyAsync(previousHolder, context, ct);
                }
                else
                {
                    var prevChar = await context.Session.LoadAsync<Character>(previousHolderId, ct);
                    if (prevChar != null)
                    {
                        await ArmorParameterResolver.ApplyAsync(prevChar, context, ct);
                        context.RegisterNewCharacter(prevChar);
                    }
                }
            }
        }

        var unequipNote = autoUnequipped ? $" (auto-unequipped from {previousHolderId})" : "";
        context.RecordMessage($"Item {transfer.ItemId} moved to {transfer.ToHolderId}{unequipNote}");

        return ChangeHandlerResult.Ok;
    }
}