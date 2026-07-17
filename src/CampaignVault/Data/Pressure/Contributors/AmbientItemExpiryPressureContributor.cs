using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Surfaces a nag for every item AmbientItemDecayRule has flagged (PressureSurfaced == true),
/// quoting the LLM's own Persistence.Note back and asking it to resolve the item's fate via
/// archive_entity, item_transfer, or item_update — the engine never decides this itself.
/// </summary>
public sealed class AmbientItemExpiryPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Item:AmbientExpiry";

    public PressureScope Scope => PressureScope.World;
    public int Order => 35;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        var allItems = await PressureQueryHelper.QueryCampaignItemsAsync(ctx.Session, ctx.CampaignName, 100, ct);

        foreach (var item in allItems)
        {
            var persistence = item.Persistence;
            if (persistence == null || !persistence.PressureSurfaced)
            {
                continue;
            }

            var noteText = string.IsNullOrWhiteSpace(persistence.Note)
                ? "no persistence note was set"
                : $"\"{persistence.Note}\"";

            pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, item.Id,
                $"'{item.Name}' has lingered past its expected time ({noteText}). Resolve its fate: " +
                "archive_entity if it's gone/cleared away, item_transfer if someone picked it up, or " +
                "item_update with a fresh ambientExpiresAtDay to give it more time.",
                GroupingKey));
        }

        return pressures;
    }
}
