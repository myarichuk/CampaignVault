using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Surfaces a nag derived from ground truth: items held in locations with an expiry date past the current
/// in-world time. Nags only when ALL of these hold:
/// - Persistence.ExpiresAtDay set AND expired (ctx.Time.TotalDaysElapsed >= ExpiresAtDay)
/// - !item.IsArchived
/// - item.HolderId starts with "locations/" (i.e., ambient at a location, not carried by a character/container)
///
/// Quoting the LLM's own Persistence.Note back and asking it to resolve the item's fate via
/// archive_entity, item_transfer, or item_update with a fresh ambientExpiresAtDay — the engine never decides this itself.
///
/// Note: Persistence.PressureSurfaced is retained for narrative one-shot event surfacing; this contributor
/// independently gates its output based on time, ensuring no dependency on flag state and silencing expired items
/// after any of the three resolution methods (archive, transfer to character, item_update).
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
            // Ground-truth check: must have expiry, be past the expiry day, not archived, and held at a location
            var persistence = item.Persistence;
            if (persistence?.ExpiresAtDay == null)
            {
                continue;
            }

            if (ctx.Time.TotalDaysElapsed < persistence.ExpiresAtDay)
            {
                continue; // Not expired yet
            }

            if (item.IsArchived)
            {
                continue; // Already archived
            }

            if (!item.HolderId?.StartsWith("locations/") ?? true)
            {
                continue; // Not ambient at a location (carried by character or in container)
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
