using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Self-healing migration for corrupted Event documents.
///
/// BACKGROUND: The Involved field holds every entity type a scene touched — character/NPC,
/// faction, quest, item, and location IDs alike. There's no dedicated field for faction/quest/item
/// references (pressure contributors such as FactionRecentEventPressureContributor read them
/// straight out of Involved), and location-scoped queries already fall back to scanning Involved
/// too (see CampaignRepository's location-filtered event queries and Event.TouchesLocation), so
/// mixing all entity types in one list is the intended contract, not corruption. The only genuine
/// corruption this repairs is malformed entries — null/empty strings — which can occur from legacy
/// code or bugs.
///
/// This is idempotent and safe to run on every startup. If no corruption is found, it
/// exits silently. If corruption IS found, it logs prominently to alert the operator.
/// </summary>
public class EventDataRepair
{
    private readonly ILogger<EventDataRepair> _logger;

    public EventDataRepair(ILogger<EventDataRepair> logger)
    {
        _logger = logger;
    }

    private const int BatchSize = 500;

    /// <summary>
    /// Scan all events for corruption and repair in-place, paging through the collection in
    /// bounded batches (each on its own session) so a large campaign history can't silently
    /// truncate at RavenDB's default result cap or load the entire Event collection into memory.
    /// Returns (count of repaired events, list of detail messages for logging).
    /// </summary>
    public async Task<(int Fixed, List<string> Details)> RepairAsync(
        IDocumentStore documentStore,
        CancellationToken ct = default)
    {
        var fixedCount = 0;
        var totalScanned = 0;
        var details = new List<string>();

        try
        {
            var skip = 0;
            while (true)
            {
                using var session = documentStore.OpenAsyncSession();
                var events = await session.Query<Event>()
                    .Where(e => e.Involved != null && e.Involved.Count > 0)
                    .OrderBy(e => e.Id)
                    .Skip(skip)
                    .Take(BatchSize)
                    .ToListAsync(ct);

                if (events.Count == 0)
                {
                    break;
                }

                totalScanned += events.Count;
                var batchFixed = 0;
                foreach (var @event in events)
                {
                    var (wasCorrupted, keptIds) = ExtractAndValidateInvolved(@event);

                    if (wasCorrupted)
                    {
                        // Repair: drop malformed (null/empty) entries; everything else stays in Involved.
                        @event.Involved = keptIds;

                        batchFixed++;
                        var detail = $"Event {FormatEventForLog(@event)}: " +
                                    $"dropped malformed entries, kept {keptIds.Count} entity ID(s)";
                        details.Add(detail);
                        _logger.LogWarning(detail);
                    }
                }

                if (batchFixed > 0)
                {
                    await session.SaveChangesAsync(ct);
                    fixedCount += batchFixed;
                }

                if (events.Count < BatchSize)
                {
                    break;
                }

                skip += BatchSize;
            }

            if (totalScanned == 0)
            {
                _logger.LogInformation("Event data repair: no events with Involved data found, nothing to check");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event data repair encountered an error and was rolled back. " +
                "Manual investigation may be needed.");
            throw;
        }

        return (fixedCount, details);
    }

    /// <summary>
    /// Check if an event's Involved field contains malformed (null/empty) entries. Every
    /// well-formed entity reference — chars/, characters/, locations/, factions/, quests/, items/,
    /// or otherwise — intentionally stays in Involved; see class remarks.
    /// Returns (isCorrupted, keptIds): keptIds is Involved with malformed entries dropped.
    /// </summary>
    private static (bool IsCorrupted, List<string> KeptIds) ExtractAndValidateInvolved(Event @event)
    {
        var keptIds = new List<string>();
        var isCorrupted = false;

        foreach (var id in @event.Involved)
        {
            if (string.IsNullOrEmpty(id))
            {
                isCorrupted = true;
                continue;
            }

            keptIds.Add(id);
        }

        return (isCorrupted, keptIds);
    }

    private static string FormatEventForLog(Event @event)
    {
        var category = @event.Category.ToString();
        var day = @event.DayLogged > 0 ? $" (Day {@event.DayLogged})" : "";
        var summary = string.IsNullOrEmpty(@event.Summary)
            ? "[no summary]"
            : @event.Summary.Length > 60
                ? @event.Summary[..57] + "..."
                : @event.Summary;

        return $"'{@event.Id}' ({category}){day}: {summary}";
    }
}
