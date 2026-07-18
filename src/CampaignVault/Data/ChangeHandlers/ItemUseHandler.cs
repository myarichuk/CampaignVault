using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ItemUseHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemUse;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var use = (ItemUse)change;

        if (string.IsNullOrWhiteSpace(use.ItemId))
        {
            return ChangeHandlerResult.Failure("itemId is required.");
        }

        if (!context.Items.TryGetValue(use.ItemId, out var item))
        {
            item = context.Session != null ? await context.Session.LoadAsync<Item>(use.ItemId, ct) : null;
            if (item == null)
            {
                var hints = await context.SuggestItemMatchAsync(use.ItemId);
                var msg = $"Item {use.ItemId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewItem(item);
        }

        if (!item.MaxCharges.HasValue)
        {
            var msg = $"Item '{use.ItemId}' has no MaxCharges set — it is not a limited-use item. Set maxCharges via world_build.";
            context.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        var oldCurrent = item.CurrentCharges ?? item.MaxCharges.Value;
        var requestedNew = oldCurrent + use.Delta;

        if (requestedNew < 0)
        {
            var msg = $"Insufficient charges on '{item.Name}': has {oldCurrent}, needs {-use.Delta}.";
            context.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        var newCurrent = Math.Clamp(requestedNew, 0, item.MaxCharges.Value);
        item.CurrentCharges = newCurrent;
        item.LastUpdated = DateTime.UtcNow;

        var narrative = use.Reason ?? (use.Delta < 0 ? "Used a charge." : "Restored charges.");
        context.RecordMessage($"{item.Name} charges: {oldCurrent} → {newCurrent}. {narrative}");

        if (newCurrent == 0 && oldCurrent > 0)
        {
            await context.LogEventAsync(new Event
            {
                Id = "events/" + Guid.NewGuid(),
                Summary = $"{item.Name} is out of charges.",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                RelatedEntityId = item.Id,
                Involved = [],
                LocationId = item.HolderId?.StartsWith("locations/", StringComparison.Ordinal) == true ? item.HolderId : null,
                DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = context.CampaignName,
            });
        }

        return ChangeHandlerResult.Ok;
    }
}
