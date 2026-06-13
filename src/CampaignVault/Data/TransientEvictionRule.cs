using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;

namespace CampaignVault.Data;

public class TransientEvictionRule : ISimulationRule
{
    private readonly ILogger<TransientEvictionRule> _logger;
    public string Name => "Transient NPC Eviction (anti-bloat)";
    
    // Runs after ScheduleEvaluationRule, Needs, RumorDecay, StatusExpiry
    public int Order => 100;

    public TransientEvictionRule(ILogger<TransientEvictionRule> logger)
    {
        _logger = logger;
    }

    public virtual async Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        // Campaign-scoped index query — avoids unscoped Take(N) truncation when the shared
        // embedded test DB accumulates characters from the full suite.
        var candidatesQuery = await SimulationQueryHelper.QueryEvictableTransientCharactersAsync(
            context.Session, context.CampaignName, 200, ct);

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
            if (c.CurrentLocationId == null)
            {
                continue; // Safety check
            }

            // Phase 7.3: Quest Giver Eviction Safety
            if (context.ActiveQuests != null && context.ActiveQuests.Any(q => q.GiverId == c.Id && (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)))
            {
                narratives.Add($"Quest giver '{c.Name}' is a transient NPC but has an active quest. Set `keepAlive: true` or assign a schedule to prevent accidental eviction.");
                continue; // Skip eviction
            }

            if (locations.TryGetValue(c.CurrentLocationId, out var loc) && loc != null)
            {
                var graceDays = context.Config?.TransientEvictionGraceDays ?? 1;
                var shouldEvict = loc.LastVisitedDay == null || (context.Time.TotalDaysElapsed - loc.LastVisitedDay.Value > graceDays);
                
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
            var orphanedItems = new List<Item>();
            foreach (var evictedId in evictedIds)
            {
                var held = await context.Session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(3))
                    .WhereEquals(x => x.HolderId, evictedId)
                    .Take(50)
                    .ToListAsync(ct);
                orphanedItems.AddRange(held);
            }

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
