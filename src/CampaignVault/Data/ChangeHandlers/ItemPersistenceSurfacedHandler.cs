using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Applies AmbientItemDecayRule's engine-authored ItemPersistenceSurfaced deltas. Not intended for
/// LLM commit use (mirrors RestRecoveryAckHandler's role for RestRecoveryAck).
/// </summary>
public sealed class ItemPersistenceSurfacedHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemPersistenceSurfaced;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var surfaced = (ItemPersistenceSurfaced)change;

        if (!context.Items.TryGetValue(surfaced.ItemId, out var item))
        {
            item = await context.Session.LoadAsync<Item>(surfaced.ItemId, ct);
            if (item == null)
            {
                return ChangeHandlerResult.Failure($"Item '{surfaced.ItemId}' not found.");
            }
            context.RegisterNewItem(item);
        }

        if (item.Persistence == null)
        {
            return ChangeHandlerResult.Ok;
        }

        item.Persistence.PressureSurfaced = true;
        return ChangeHandlerResult.Ok;
    }
}
