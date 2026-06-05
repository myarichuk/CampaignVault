using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Linq;

namespace CampaignVault.Data;

public sealed class TransientEvictionRule : ISimulationRule
{
    private readonly ILogger<TransientEvictionRule> _logger;
    public string Name => "Transient NPC Eviction (anti-bloat)";
    
    // Runs after ScheduleEvaluationRule, Needs, RumorDecay, StatusExpiry
    public int Order => 100;

    public TransientEvictionRule(ILogger<TransientEvictionRule> logger)
    {
        _logger = logger;
    }

    public async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        // 1. Query candidates: Schedule == null && CurrentLocationId != null && !KeepAlive
        // Scoping hardened: filter by camp (loose for shareable chars)
        var effective = context.CampaignName;
        // RavenDB dynamic index bug workaround: do not filter by KeepAlive or Schedule in DB
        var candidates = await context.Session.Query<Character>()
            .Where(c => c.CurrentLocationId != null)
            .ToListAsync(ct);

        if (!string.IsNullOrEmpty(effective))
        {
            candidates = candidates.Where(c => string.IsNullOrEmpty(c.CampaignName) || string.Equals(c.CampaignName, effective, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Filter out characters that have a schedule or are KeepAlive (only transients should be evicted)
        candidates = candidates.Where(c => c.Schedule == null && c.KeepAlive == false).ToList();

        var candidatesQuery = candidates.Take(200).ToList();

        if (!candidatesQuery.Any())
        {
            return new RuleResult(narratives, deltas);
        }

        // 2. Collect unique CurrentLocationIds and Load Locations
        var locationIds = candidatesQuery.Select(c => c.CurrentLocationId!).Distinct().ToList();
        var locations = await context.Session.LoadAsync<Location>(locationIds, ct);

        var evictedIds = new List<string>();

        // 3. Evaluate each candidate
        foreach (var c in candidatesQuery)
        {
            if (c.CurrentLocationId == null) continue; // Safety check

            // Phase 7.3: Quest Giver Eviction Safety
            if (context.ActiveQuests != null && context.ActiveQuests.Any(q => q.GiverId == c.Id && (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)))
            {
                narratives.Add($"Quest giver '{c.Name}' is a transient NPC but has an active quest. Set `keepAlive: true` or assign a schedule to prevent accidental eviction.");
                continue; // Skip eviction
            }

            if (locations.TryGetValue(c.CurrentLocationId, out var loc) && loc != null)
            {
                bool shouldEvict = loc.LastVisitedDay == null || (context.Time.TotalDaysElapsed - loc.LastVisitedDay.Value > 1);
                
                if (shouldEvict)
                {
                    deltas.Add(new ActivityChange
                    {
                        CharacterId = c.Id,
                        NewLocationId = null,
                        UpdateLocation = true,
                        NewActivity = "drifted away / area has quieted since the party left",
                        Reason = "Engine transient eviction — location unvisited for >1 campaign day"
                    });
                    narratives.Add($"{c.Name} is no longer present in {loc.Name} (the area has gone cold).");
                    evictedIds.Add(c.Id);
                }
            }
            else
            {
                // The location doesn't exist anymore; evict the orphaned transient
                deltas.Add(new ActivityChange
                {
                    CharacterId = c.Id,
                    NewLocationId = null,
                    UpdateLocation = true,
                    NewActivity = "wandered off after their surroundings changed",
                    Reason = "Engine transient eviction — orphaned transient"
                });
                evictedIds.Add(c.Id);
            }
        }

        if (evictedIds.Any())
        {
            var orphanedItems = await context.Session.Query<Item>()
                .Where(i => i.HolderId.In(evictedIds))
                .ToListAsync(ct);

            foreach (var item in orphanedItems)
            {
                context.Session.Delete(item);
            }
            if (orphanedItems.Any())
            {
                narratives.Add($"Evicted {orphanedItems.Count} orphaned items held by transient characters.");
            }
        }

        if (narratives.Any())
        {
            _logger.LogInformation("TransientEvictionRule evicted {Count} transient characters.", evictedIds.Count);
        }

        return new RuleResult(narratives, deltas);
    }
}
