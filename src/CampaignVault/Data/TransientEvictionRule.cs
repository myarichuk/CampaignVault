using CampaignVault.Models;

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

        var candidatesQuery = await SimulationQueryHelper.QueryEvictableTransientCharactersAsync(
            context.Session, context.CampaignName, 200, ct);

        if (!candidatesQuery.Any())
        {
            return new RuleResult(narratives, deltas);
        }

        var locationIds = candidatesQuery.Select(c => c.CurrentLocationId!).Distinct().ToList();
        var locations = await context.Session.LoadAsync<Location>(locationIds, ct);

        var evictedIds = new List<string>();
        var evictedSummaries = new List<EvictedNpcSummary>();
        var currentDay = context.Time.TotalDaysElapsed;

        foreach (var c in candidatesQuery)
        {
            if (c.CurrentLocationId == null)
            {
                continue;
            }

            if (context.ActiveQuests != null && context.ActiveQuests.Any(q =>
                    q.GiverId == c.Id && (q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)))
            {
                narratives.Add(
                    $"Quest giver '{c.Name}' is a transient NPC but has an active quest. Set `keepAlive: true` or assign a schedule to prevent accidental eviction.");
                continue;
            }

            var fromLocationId = c.CurrentLocationId;
            Location? fromLoc = null;
            var shouldEvict = false;
            var evictionReason = "Engine transient eviction — orphaned transient";

            if (locations.TryGetValue(fromLocationId, out fromLoc) && fromLoc != null)
            {
                var graceDays = context.Config?.TransientEvictionGraceDays ?? 1;
                shouldEvict = fromLoc.LastVisitedDay == null ||
                              (currentDay - fromLoc.LastVisitedDay.Value > graceDays);
                evictionReason = "Engine transient eviction — location unvisited for > grace period";
            }
            else
            {
                shouldEvict = true;
            }

            if (!shouldEvict)
            {
                continue;
            }

            var fromLocationName = fromLoc?.Name;
            var activityNote = fromLoc != null
                ? "drifted away / area has quieted since the party left"
                : "wandered off after their surroundings changed";

            deltas.Add(new ActivityChange
            {
                CharacterId = c.Id,
                NewLocationId = null,
                UpdateLocation = true,
                NewActivity = activityNote,
                Reason = evictionReason
            });

            deltas.Add(new EventOccurred
            {
                Summary = fromLocationName != null
                    ? $"{c.Name} departed {fromLocationName} ({activityNote})."
                    : $"{c.Name} departed ({activityNote}).",
                Category = EventCategory.Departure,
                Involved = [c.Id],
                RelatedEntityId = fromLocationId
            });

            if (fromLoc != null)
            {
                deltas.Add(new LocationUpdate
                {
                    LocationId = fromLocationId,
                    RecordDeparture = new DepartedNpcRecord(c.Id, c.Name, currentDay, evictionReason)
                });
            }

            deltas.Add(new CharacterUpdate
            {
                CharacterId = c.Id,
                DepartedAtDay = currentDay,
                DepartedFromLocationId = fromLocationId
            });

            narratives.Add(fromLocationName != null
                ? $"{c.Name} is no longer present in {fromLocationName} (the area has gone cold)."
                : $"{c.Name} is no longer present (orphaned transient).");
            evictedIds.Add(c.Id);
            evictedSummaries.Add(new EvictedNpcSummary(c.Id, c.Name, fromLocationId, fromLocationName));
        }

        if (evictedIds.Count > 0)
        {
            var transferredItems = 0;
            foreach (var evictedId in evictedIds)
            {
                var summary = evictedSummaries.First(s => s.CharacterId == evictedId);
                var dropLocationId = summary.FromLocationId;
                if (string.IsNullOrWhiteSpace(dropLocationId))
                {
                    continue;
                }

                var held = await context.Session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(3))
                    .WhereEquals(x => x.HolderId, evictedId)
                    .Take(50)
                    .ToListAsync(ct);

                foreach (var item in held)
                {
                    deltas.Add(new ItemTransfer
                    {
                        ItemId = item.Id,
                        ToHolderId = dropLocationId
                    });
                    transferredItems++;
                }
            }

            if (transferredItems > 0)
            {
                narratives.Add(
                    $"Transferred {transferredItems} item(s) left behind by evicted transients to their last known locations.");
            }

            _logger.LogInformation("TransientEvictionRule evicted {Count} transient characters.", evictedIds.Count);
        }

        return new RuleResult(
            narratives,
            deltas,
            evictedIds.Count > 0 ? evictedIds.AsReadOnly() : null,
            evictedSummaries.Count > 0 ? evictedSummaries.AsReadOnly() : null);
    }
}