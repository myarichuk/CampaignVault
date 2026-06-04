using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

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
        var candidates = await context.Session.Query<Character>()
            .Where(c => c.Schedule == null && c.CurrentLocationId != null && !c.KeepAlive)
            .ToListAsync(ct);
        if (!string.IsNullOrEmpty(effective))
        {
            candidates = candidates.Where(c => string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective).ToList();
        }
        var candidatesQuery = candidates.Take(200).ToList();

        if (!candidatesQuery.Any())
        {
            return new RuleResult(narratives, deltas);
        }

        // 2. Collect unique CurrentLocationIds and Load Locations
        var locationIds = candidatesQuery.Select(c => c.CurrentLocationId!).Distinct().ToList();
        var locations = await context.Session.LoadAsync<Location>(locationIds, ct);

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
                // Evict if location was never visited or hasn't been visited in > 1 day
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
                    NewActivity = "drifted away",
                    Reason = "Engine transient eviction — location missing"
                });
                narratives.Add($"{c.Name} drifted away.");
            }
        }

        if (narratives.Any())
        {
            _logger.LogInformation("TransientEvictionRule evicted {Count} transient characters.", narratives.Count);
        }

        return new RuleResult(narratives, deltas);
    }
}
