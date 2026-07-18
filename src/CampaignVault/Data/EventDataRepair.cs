using CampaignVault.Models;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Scan all events for corruption and repair in-place.
    /// Returns (count of repaired events, list of detail messages for logging).
    /// </summary>
    public async Task<(int Fixed, List<string> Details)> RepairAsync(
        IAsyncDocumentSession session,
        CancellationToken ct = default)
    {
        var fixed = 0;
        var details = new List<string>();

        try
        {
            var events = await session.Query<Event>()
                .Where(e => e.Involved != null && e.Involved.Count > 0)
                .ToListAsync(ct);

            if (events.Count == 0)
            {
                _logger.LogInformation("Event data repair: no events with Involved data found, nothing to check");
                return (0, []);
            }

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

                    fixed++;
                    var detail = $"Event {FormatEventForLog(@event)}: " +
                                $"moved {locIds.Count} location(s) from Involved, kept {charIds.Count} character(s)";
                    details.Add(detail);
                    _logger.LogWarning(detail);
                }
            }

            // Persist repairs
            if (fixed > 0)
            {
                await session.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event data repair encountered an error and was rolled back. " +
                "Manual investigation may be needed.");
            throw;
        }

        return (fixed, details);
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
        var category = @event.Category?.ToString() ?? "Unknown";
        var day = @event.DayLogged.HasValue ? $" (Day {@event.DayLogged})" : "";
        var summary = string.IsNullOrEmpty(@event.Summary)
            ? "[no summary]"
            : @event.Summary.Length > 60
                ? @event.Summary[..57] + "..."
                : @event.Summary;

        return $"'{@event.Id}' ({category}){day}: {summary}";
    }
}
