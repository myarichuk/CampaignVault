using CampaignVault.Models;
using Raven.Client.Documents.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CampaignVault.Data;

public interface IPressureManager
{
    Task<string[]> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay, IEnumerable<WorldPressureItem> rawPressures);
}

public class PressureManager(CampaignDocumentKeys keys) : IPressureManager
{
    public async Task<string[]> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay, IEnumerable<WorldPressureItem> rawPressures)
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
            // Fallback for tests or broken state
            var fallbackGroups = pressures
                .Select(p => (Item: p, OriginalKey: $"{p.Severity}:{p.EntityId}", Escalated: false))
                .GroupBy(x => new { x.Item.GroupingKey, x.Item.Severity, x.Escalated })
                .OrderByDescending(g => g.Key.Severity)
                .Take(5)
                .ToList();
            return FormatBatches(fallbackGroups, 9); // Fallback assumption
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

        return FormatBatches(groups, cooldownDays * escalationCount);
    }

    private string[] FormatBatches<TKey>(IEnumerable<IGrouping<TKey, (WorldPressureItem Item, string OriginalKey, bool Escalated)>> groups, int escalationDays)
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
