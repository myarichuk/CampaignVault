using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public interface IPressureManager
{
    /// <summary>
    /// Filters and caps the raw pressures based on cooldowns and configuration.
    /// Returns the surfaced items (after cooldown, escalation, max cap). Callers should use ToDisplayStrings
    /// to get the human/LLM text form (which includes SuggestedCommitJson when present).
    /// Note: This method mutates the campaign's PressureCooldowns state. The caller is responsible for 
    /// calling SaveChangesAsync() on the session to persist these cooldowns.
    /// </summary>
    Task<List<WorldPressureItem>> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay,
        IEnumerable<WorldPressureItem> rawPressures, bool disableCooldowns = false);
}

public class PressureManager(CampaignDocumentKeys keys, ILogger<PressureManager>? logger = null) : IPressureManager
{
    public async Task<List<WorldPressureItem>> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay,
        IEnumerable<WorldPressureItem>? rawPressures, bool disableCooldowns = false)
    {
        var pressures = rawPressures?.ToList() ?? [];
        if (pressures.Count == 0)
        {
            return [];
        }

        var metaId = keys.Meta(campaignName);
        var campaign = await session.LoadAsync<Campaign>(metaId);
        if (campaign == null)
        {
            logger?.LogError(
                "Campaign document '{CampaignName}' not found. Will throw an error since its a broken invariant.",
                campaignName);

            throw new InvalidOperationException(
                $"Campaign document for '{campaignName}' not found - cannot calculate pressure (looked for '{metaId}')");
        }

        var configId = keys.Config(campaignName);
        var config = await session.LoadAsync<CampaignConfig>(configId);
        var maxPressures = config?.MaxPressuresPerResponse ?? 5;
        var cooldownDays = config?.PressureCooldownDays ?? 1;
        var escalationCount = config?.PressureEscalationCount ?? 3;

        var finalItems = new List<(WorldPressureItem Item, string OriginalKey, bool Escalated)>();

        foreach (var p in pressures)
        {
            // GroupingKey is part of the key, not just Severity:EntityId — two different contributors
            // can both flag the same entity at EngineWarning (e.g. IncompleteSystemStatsPressureContributor's
            // "uninitialized systemStats" and CharacterDistressPressureContributor's "no MaxHp set" are both
            // triggered by the same unbootstrapped character). Without GroupingKey in the key, those two
            // differently-worded nags collide on one cooldown slot and perpetually look "changed" to each
            // other, defeating the cooldown entirely — the entity nags every turn instead of once per
            // PressureCooldownDays.
            var key = $"{p.Severity}:{p.GroupingKey}:{p.EntityId}";
            var signature = PressureHelpers.ComputeContentSignature(p.Text);
            if (campaign.PressureCooldowns.TryGetValue(key, out var state))
            {
                // A stored signature that differs from this item's means the underlying nag content
                // materially changed (not just an embedded numeric value) — treat it as a fresh nag
                // rather than suppressing it or inheriting the prior escalation cycle.
                var signatureChanged = state.LastSignature is not null && state.LastSignature != signature;

                if (!signatureChanged && currentDay - state.LastSurfacedDay < cooldownDays)
                {
                    // Suppressed
                    continue;
                }

                var newCount = signatureChanged ? 1 : state.SuppressionCount + 1;
                if (newCount >= escalationCount)
                {
                    // Escalated
                    var escalatedItem = p with { Severity = PressureSeverity.EngineWarning };
                    finalItems.Add((escalatedItem, key, true));
                }
                else
                {
                    finalItems.Add((p, key, false));
                }
            }
            else
            {
                finalItems.Add((p, key, false));
            }
        }

        // Group by GroupingKey (and Severity) to batch similar alerts
        var allGroups = finalItems
            .GroupBy(x => new { x.Item.GroupingKey, x.Item.Severity, x.Escalated })
            .OrderByDescending(g => g.Key.Severity)
            .ToList();

        // Cap at the item level: accumulate whole groups while item count stays under maxPressures.
        // Always include at least one group, even if it alone exceeds the cap.
        var cappedGroups = new List<IGrouping<dynamic, (WorldPressureItem Item, string OriginalKey, bool Escalated)>>();
        int itemCount = 0;
        foreach (var group in allGroups)
        {
            if (cappedGroups.Count == 0 || itemCount + group.Count() <= maxPressures)
            {
                cappedGroups.Add((IGrouping<dynamic, (WorldPressureItem Item, string OriginalKey, bool Escalated)>)group);
                itemCount += group.Count();
            }
            else if (cappedGroups.Count == 0)
            {
                // Always include at least the first (most severe) group, even if it exceeds the cap
                cappedGroups.Add((IGrouping<dynamic, (WorldPressureItem Item, string OriginalKey, bool Escalated)>)group);
                itemCount = group.Count();
                break;
            }
            else
            {
                break;
            }
        }

        var cappedItems = cappedGroups.SelectMany(g => g).Select(t => t.Item).ToList();
        var groups = cappedGroups;

        if (!disableCooldowns)
        {
            // Update tracking for surfaced items
            foreach (var tuple in groups.SelectMany(g => g))
            {
                var key = tuple.OriginalKey;
                var signature = PressureHelpers.ComputeContentSignature(tuple.Item.Text);

                if (campaign.PressureCooldowns.TryGetValue(key, out var existingState))
                {
                    var signatureChanged = existingState.LastSignature is not null && existingState.LastSignature != signature;
                    campaign.PressureCooldowns[key] = existingState with
                    {
                        LastSurfacedDay = currentDay,
                        SuppressionCount = signatureChanged ? 1 : existingState.SuppressionCount + 1,
                        LastSignature = signature
                    };
                }
                else
                {
                    campaign.PressureCooldowns[key] = new PressureState(currentDay, 0) { LastSignature = signature };
                }
            }
        }

        return cappedItems;
    }

    /// <summary>
    /// Formats pressure items into the display strings used in ToolResult.WorldPressure (legacy text channel).
    /// Uses Abbreviation field if present (terse, ~20 chars), falls back to Text. Includes SuggestedCommitJson inline.
    /// Attempts light batching by GroupingKey. Reduces per-turn chattiness ~100-150 tokens per scene.
    /// </summary>
    public static string[] ToDisplayStrings(IEnumerable<WorldPressureItem>? items)
    {
        if (items == null) return [];
        var list = items.ToList();
        if (list.Count == 0) return [];

        // Simple grouping for batch display (mirrors prior behavior)
        var groups = list
            .GroupBy(p => new { p.GroupingKey, p.Severity })
            .OrderByDescending(g => g.Key.Severity)
            .ToList();

        return groups.Select(g =>
        {
            var first = g.First();
            var prefix = first.Severity switch
            {
                PressureSeverity.EngineWarning => "ENGINE WARNING",
                PressureSeverity.NarrativePrompt => "NARRATIVE PROMPT",
                PressureSeverity.Simulation => "SIMULATION PRESSURE",
                PressureSeverity.Suggestion => "SUGGESTION",
                _ => "PRESSURE"
            };

            var itemsInGroup = g.ToList();
            string body;
            if (itemsInGroup.Count == 1)
            {
                var item = itemsInGroup[0];
                // Use abbreviation if present (terse, ~20 chars), else fall back to full text
                body = item.Abbreviation ?? item.Text;
            }
            else
            {
                var keyParts = first.GroupingKey.Split(':');
                var category = keyParts.Length > 1 ? string.Join(" ", keyParts.Skip(1)) : first.GroupingKey;
                // For batched items, use full text (preserves test assertions on names/values)
                var batched = string.Join(" | ", itemsInGroup.Select(x => x.Text));
                body = $"({itemsInGroup.Count} similar issues - {category}): {batched}";
            }

            var text = $"{prefix}: {body}";
            // Append suggested from first in group if present (or could concat but one is enough)
            var suggested = itemsInGroup.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SuggestedCommitJson))?.SuggestedCommitJson;
            if (!string.IsNullOrWhiteSpace(suggested))
            {
                text += $"\nSuggested commit:\n{suggested}";
            }
            return text;
        }).ToArray();
    }
}