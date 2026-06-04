using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class ItemCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var ic = (ItemCreate)change;
        if (string.IsNullOrWhiteSpace(ic.ItemId))
            return ChangeHandlerResult.Failure("itemId is required.");

        var existing = context.Session != null ? await context.Session.LoadAsync<Item>(ic.ItemId, ct) : null;
        if (existing != null)
        {
            existing.Name = ic.Name ?? existing.Name;
            if (ic.Description != null) existing.Description = ic.Description;
            if (ic.HolderId != null) existing.HolderId = ic.HolderId;
            if (ic.Tags != null && ic.Tags.Count > 0) existing.Tags = ic.Tags;
            if (ic.Properties != null && ic.Properties.Count > 0)
                existing.Properties = ic.Properties.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                
            context.RecordMessage($"Warning: Item {ic.ItemId} already exists. Updated existing fields.");
            return ChangeHandlerResult.Ok;
        }

        var newItem = new Item
        {
            Id = ic.ItemId,
            Name = ic.Name ?? "Unnamed Item",
            Description = ic.Description ?? "",
            HolderId = ic.HolderId,
            Tags = ic.Tags ?? [],
            Properties = ic.Properties?.ToDictionary(kv => kv.Key, kv => (object)kv.Value) ?? new Dictionary<string, object>()
        };

        if (string.IsNullOrEmpty(newItem.CampaignName))
            newItem.CampaignName = context.CampaignName;

        await context.Session.StoreAsync(newItem, ct);
        context.RegisterNewItem(newItem);

        return ChangeHandlerResult.Ok;
    }
}
