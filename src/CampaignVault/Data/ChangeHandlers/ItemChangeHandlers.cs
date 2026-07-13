using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

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
