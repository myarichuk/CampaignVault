using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class DanglingItemPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Item:DanglingHolder";

    public PressureScope Scope => PressureScope.World;
    public int Order => 30;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        var allItems = await PressureQueryHelper.QueryCampaignItemsAsync(ctx.Session, ctx.CampaignName, 100, ct);

        foreach (var item in allItems)
        {
            if ((string.IsNullOrEmpty(item.CampaignName) || item.CampaignName == ctx.CampaignName) && !string.IsNullOrEmpty(item.HolderId))
            {
                var holderExists = await ctx.Session.Advanced.ExistsAsync(item.HolderId, ct);
                if (!holderExists)
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, item.Id,
                        $"Item '{item.Name}' is held by '{item.HolderId}' which no longer exists (likely GC'd). " +
                        "Use $type item with toHolderId to move it to a valid location or character:\n" +
                        "[ { \"$type\": \"item\", \"itemId\": \"" + item.Id + "\", \"toHolderId\": \"locations/some_valid_location\" } ]",
                        GroupingKey));
                }
            }
        }

        return pressures;
    }
}