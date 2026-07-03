using CampaignVault.Data;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

/// <summary>
/// Surfaces suggest-only event consequence commits on get_scene (5a). Does not auto-apply.
/// </summary>
public sealed class EventConsequencePressureContributor : IPressureContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 45;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene == null || !ctx.Scene.IsLocationAnchored)
        {
            return pressures;
        }

        var locId = ctx.Scene.Location.Id;
        var minDay = Math.Max(0, (int)ctx.Time.TotalDaysElapsed - 7);
        var recentEvents = await PressureQueryHelper.QueryEventConsequenceCandidatesAsync(
            ctx.Session, ctx.CampaignName, minDay, 50, ct);

        var locationEvents = recentEvents
            .Where(e => string.Equals(e.RelatedEntityId, locId, StringComparison.OrdinalIgnoreCase)
                        || (e.Involved?.Contains(locId, StringComparer.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(e => e.Timestamp)
            .Take(5);

        foreach (var evt in locationEvents)
        {
            if (!EventConsequenceRegistry.TrySuggest(evt, out var templateId, out var suggestedJson))
            {
                continue;
            }

            pressures.Add(new WorldPressureItem(
                PressureSeverity.NarrativePrompt,
                locId,
                $"Recent {evt.Category} event '{evt.Summary}' may warrant a location or relationship update. " +
                $"Template: {templateId}. Apply via commit if narratively appropriate (suggest-only).",
                $"{EventConsequenceRegistry.EventConsequenceGroupingKey}:{templateId}")
            {
                SuggestedCommitJson = suggestedJson
            });
        }

        return pressures;
    }
}