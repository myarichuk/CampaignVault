using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public interface IPressureManager
{
    /// <summary>
    /// Filters and caps the raw pressures based on cooldowns and configuration.
    /// Note: This method mutates the campaign's PressureCooldowns state. The caller is responsible for 
    /// calling SaveChangesAsync() on the session to persist these cooldowns.
    /// </summary>
    Task<string[]> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay,
        IEnumerable<WorldPressureItem> rawPressures, bool disableCooldowns = false);
}

public class PressureManager(CampaignDocumentKeys keys, ILogger<PressureManager>? logger = null) : IPressureManager
{
    public async Task<string[]> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay,
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

        var cappedItems = groups.SelectMany(g => g).ToList();

        if (!disableCooldowns)
        {
            // Update tracking for surfaced items
            foreach (var tuple in cappedItems)
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

        return FormatBatches(groups, cooldownDays * escalationCount);
    }

    private string[] FormatBatches<TKey>(
        IEnumerable<IGrouping<TKey, (WorldPressureItem Item, string OriginalKey, bool Escalated)>> groups,
        int escalationDays)
    {
        return groups.Select(g =>
        {
            var first = g.First();
            var prefix = first.Item.Severity switch
            {
                PressureSeverity.EngineWarning => "ENGINE WARNING",
                PressureSeverity.NarrativePrompt => "NARRATIVE PROMPT",
                PressureSeverity.Simulation => "SIMULATION PRESSURE",
                PressureSeverity.Suggestion => "SUGGESTION",
                _ => "PRESSURE"
            };

            if (first.Escalated)
            {
                prefix = $"{prefix} [ESCALATED: Flagged for >{escalationDays} days]";
            }

            var items = g.ToList();
            if (items.Count == 1)
            {
                return $"{prefix}: {items[0].Item.Text}";
            }
            else
            {
                var keyParts = first.Item.GroupingKey.Split(':');
                var category = keyParts.Length > 1 ? string.Join(" ", keyParts.Skip(1)) : first.Item.GroupingKey;

                var batchedText = string.Join(" | ", items.Select(x => x.Item.Text));
                return $"{prefix} ({items.Count} similar issues - {category}): {batchedText}";
            }
        }).ToArray();
    }
}