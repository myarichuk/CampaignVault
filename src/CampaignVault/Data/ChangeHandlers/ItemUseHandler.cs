using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ItemUseHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemUse;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var use = (ItemUse)change;

        if (string.IsNullOrWhiteSpace(use.ItemId))
        {
            return ChangeHandlerResult.Failure("itemId is required.");
        }

        if (!ctx.Items.TryGetValue(use.ItemId, out var item))
        {
            item = await ctx.Session.LoadAsync<Item>(use.ItemId, ct);
            if (item == null)
            {
                var hints = await ctx.SuggestItemMatchAsync(use.ItemId);
                var msg = $"Item {use.ItemId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                ctx.RecordMessage($"WARNING: {msg}");
                ctx.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            ctx.RegisterNewItem(item);
        }

        if (!item.MaxCharges.HasValue)
        {
            var msg = $"Item '{use.ItemId}' has no MaxCharges set — it is not a limited-use item. Set maxCharges via world_build.";
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        var oldCurrent = item.CurrentCharges ?? item.MaxCharges.Value;
        var requestedNew = oldCurrent + use.Delta;

        if (requestedNew < 0)
        {
            var msg = $"Insufficient charges on '{item.Name}': has {oldCurrent}, needs {-use.Delta}.";
            ctx.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        var newCurrent = Math.Clamp(requestedNew, 0, item.MaxCharges.Value);
        item.CurrentCharges = newCurrent;
        item.LastUpdated = DateTime.UtcNow;

        // Don't echo use.Reason back — the caller just supplied that exact text in this same
        // request; repeating it costs tokens for zero new information.
        ctx.RecordMessage($"{item.Name} charges: {oldCurrent} → {newCurrent}.");

        if (newCurrent == 0 && oldCurrent > 0)
        {
            await ctx.LogEventAsync(new Event
            {
                Id = "events/" + Guid.NewGuid(),
                Summary = $"{item.Name} is out of charges.",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                RelatedEntityId = item.Id,
                Involved = [],
                LocationId = item.HolderId?.StartsWith("locations/", StringComparison.Ordinal) == true ? item.HolderId : null,
                DayLogged = (await ctx.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = ctx.CampaignName,
            });
        }

        return ChangeHandlerResult.Ok;
    }
}
