using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class ItemCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var ic = (ItemCreate)change;
        if (string.IsNullOrWhiteSpace(ic.ItemId))
        {
            return ChangeHandlerResult.Failure("itemId is required.");
        }

        var existing = context.Session != null ? await context.Session.LoadAsync<Item>(ic.ItemId, ct) : null;
        if (existing != null)
        {
            existing.Name = ic.Name ?? existing.Name;
            if (ic.Description != null)
            {
                existing.Description = ic.Description;
            }

            if (ic.HolderId != null)
            {
                existing.HolderId = ic.HolderId;
            }

            if (ic.Tags != null && ic.Tags.Count > 0)
            {
                existing.Tags = ic.Tags;
            }

            if (ic.Properties != null && ic.Properties.Count > 0)
            {
                existing.Properties = ic.Properties.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            }

            if (ic.CoreCategory.HasValue)
            {
                existing.CoreCategory = ic.CoreCategory.Value;
            }

            context.RecordMessage($"Warning: Item {ic.ItemId} already exists. Updated existing fields.");
            return ChangeHandlerResult.Ok;
        }

        var newItem = new Item
        {
            Id = ic.ItemId,
            Name = ic.Name ?? "Unnamed Item",
            Description = ic.Description ?? "",
            HolderId = ic.HolderId,
            CoreCategory = ic.CoreCategory ?? ItemCategory.Other,
            Tags = ic.Tags ?? [],
            Properties = ic.Properties?.ToDictionary(kv => kv.Key, kv => (object)kv.Value) ?? new Dictionary<string, object>()
        };

        if (string.IsNullOrEmpty(newItem.CampaignName))
        {
            newItem.CampaignName = context.CampaignName;
        }

        await context.Session!.StoreAsync(newItem, ct);
        context.RegisterNewItem(newItem);

        return ChangeHandlerResult.Ok;
    }
}

public class ItemUpdateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var iu = (ItemUpdate)change;
        if (string.IsNullOrWhiteSpace(iu.ItemId)) return ChangeHandlerResult.Failure("itemId is required.");

        var item = context.Session != null ? await context.Session.LoadAsync<Item>(iu.ItemId, ct) : null;
        if (item == null) return ChangeHandlerResult.Failure($"Item '{iu.ItemId}' not found. Cannot update.");

        if (iu.NewState != null) item.CurrentState = iu.NewState;
        if (iu.CoreCategory.HasValue) item.CoreCategory = iu.CoreCategory.Value;

        if (iu.TagsToAdd != null)
        {
            item.Tags = item.Tags.Union(iu.TagsToAdd).Distinct().ToList();
        }
        if (iu.TagsToRemove != null)
        {
            item.Tags.RemoveAll(t => iu.TagsToRemove.Contains(t));
        }

        if (iu.FeaturesToAdd != null)
        {
            item.DistinctiveFeatures = item.DistinctiveFeatures.Union(iu.FeaturesToAdd).Distinct().ToList();
        }
        if (iu.FeaturesToRemove != null)
        {
            item.DistinctiveFeatures.RemoveAll(f => iu.FeaturesToRemove.Contains(f));
        }

        if (iu.PropertiesToUpsert != null)
        {
            foreach (var kv in iu.PropertiesToUpsert) item.Properties[kv.Key] = kv.Value;
        }
        if (iu.PropertiesToRemove != null)
        {
            foreach (var k in iu.PropertiesToRemove) item.Properties.Remove(k);
        }

        context.RecordMessage($"Updated state/tags for item '{iu.ItemId}'.");
        return ChangeHandlerResult.Ok;
    }
}
