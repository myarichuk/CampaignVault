using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

/// <summary>
/// Self-healing migration for corrupted Event documents.
///
/// BACKGROUND: The Involved field should ONLY contain character/NPC IDs. Location, item,
/// faction, and quest IDs should use dedicated fields. This repair routine detects and fixes
/// events that violate this contract, which can occur from legacy code or bugs.
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
                    var (wasCorrupted, charIds, locIds) = ExtractAndValidateInvolved(@event);

                    if (wasCorrupted)
                    {
                        // Repair: move non-character IDs to proper fields
                        @event.Involved = charIds;

                        if (locIds.Any())
                        {
                            if (@event.RelatedLocationIds == null)
                                @event.RelatedLocationIds = locIds;
                            else
                            {
                                var merged = new HashSet<string>(@event.RelatedLocationIds, StringComparer.OrdinalIgnoreCase);
                                foreach (var loc in locIds)
                                    merged.Add(loc);
                                @event.RelatedLocationIds = merged.ToList();
                            }
                        }

                        batchFixed++;
                        var detail = $"Event {FormatEventForLog(@event)}: " +
                                    $"moved {locIds.Count} location(s) from Involved, kept {charIds.Count} character(s)";
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
    /// Check if an event's Involved field contains non-character IDs.
    /// Returns (isCorrupted, characterIds, locationIds).
    /// </summary>
    private static (bool IsCorrupted, List<string> CharacterIds, List<string> LocationIds)
        ExtractAndValidateInvolved(Event @event)
    {
        var charIds = new List<string>();
        var locIds = new List<string>();
        var isCorrupted = false;

        foreach (var id in @event.Involved)
        {
            if (string.IsNullOrEmpty(id))
            {
                isCorrupted = true;
                continue;
            }

            if (id.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
            {
                charIds.Add(id);
            }
            else if (id.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
            {
                locIds.Add(id);
                isCorrupted = true;
            }
            else if (id.StartsWith("factions/", StringComparison.OrdinalIgnoreCase) ||
                     id.StartsWith("quests/", StringComparison.OrdinalIgnoreCase) ||
                     id.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
            {
                // Non-character entity type in Involved field (corruption)
                isCorrupted = true;
            }
            else
            {
                // Unrecognized ID format (potential corruption)
                isCorrupted = true;
            }
        }

        return (isCorrupted, charIds, locIds);
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
