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
        IEnumerable<WorldPressureItem> rawPressures, bool disableCooldowns = false)
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
            var key = $"{p.Severity}:{p.EntityId}";
            if (campaign.PressureCooldowns.TryGetValue(key, out var state))
            {
                if (currentDay - state.LastSurfacedDay < cooldownDays)
                {
                    // Suppressed
                    continue;
                }

                var newCount = state.SuppressionCount + 1;
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
        var groups = finalItems
            .GroupBy(x => new { x.Item.GroupingKey, x.Item.Severity, x.Escalated })
            .OrderByDescending(g => g.Key.Severity)
            .Take(maxPressures)
            .ToList();

        var cappedItems = groups.SelectMany(g => g).Select(t => t.Item).ToList();

        if (!disableCooldowns)
        {
            // Update tracking for surfaced items
            foreach (var tuple in groups.SelectMany(g => g))
            {
                var key = tuple.OriginalKey;

                if (campaign.PressureCooldowns.TryGetValue(key, out var existingState))
                {
                    campaign.PressureCooldowns[key] = existingState with
                    {
                        LastSurfacedDay = currentDay,
                        SuppressionCount = existingState.SuppressionCount + 1
                    };
                }
                else
                {
                    campaign.PressureCooldowns[key] = new PressureState(currentDay, 0);
                }
            }
        }

        return cappedItems;
    }

    /// <summary>
    /// Formats pressure items into the display strings used in ToolResult.WorldPressure (legacy text channel).
    /// Includes SuggestedCommitJson inline when present on an item. Attempts light batching by GroupingKey.
    /// </summary>
    public static string[] ToDisplayStrings(IEnumerable<WorldPressureItem> items)
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
                body = itemsInGroup[0].Text;
            }
            else
            {
                var keyParts = first.GroupingKey.Split(':');
                var category = keyParts.Length > 1 ? string.Join(" ", keyParts.Skip(1)) : first.GroupingKey;
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