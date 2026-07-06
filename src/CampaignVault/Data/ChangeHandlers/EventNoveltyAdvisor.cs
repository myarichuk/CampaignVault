using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Advisory importance/novelty scoring for a newly logged event: compares its embedding
/// (already computed on save by CampaignRepository.LogEventAsync — no extra model call) against
/// recent events in the same campaign, and hints when the beat looks novel (candidate for
/// Important/Core memory + plot-thread follow-up) or looks like a repeat (candidate for Trivial).
/// Pure hint text appended to the commit summary — never blocks or fails the commit, and never
/// runs when no embedding is available (e.g. in unit tests that bypass real persistence).
/// </summary>
internal static class EventNoveltyAdvisor
{
    private const double LowSimilarityThreshold = 0.55;
    private const double HighSimilarityThreshold = 0.90;
    private const int RecentEventsToCompare = 5;

    public static async Task<(double? Similarity, string? Hint)> ScoreAsync(ChangeContext context, Event newEvent, CancellationToken ct = default)
    {
        if (newEvent.SemanticVector is not { Length: > 0 } vector || context.Session == null)
        {
            return (null, null);
        }

        List<Event> recent;
        try
        {
            recent = await context.Session.Query<Event, Event_Search>()
                .Where(x => x.CampaignName == context.CampaignName)
                .OrderByDescending(x => x.Timestamp)
                .Take(RecentEventsToCompare)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // Advisory only — a stale/unavailable index must never affect commit success.
            context.Logger.LogDebug(ex, "EventNoveltyAdvisor: skipped novelty scoring (advisory only).");
            return (null, null);
        }

        var comparable = recent.Where(e => e.Id != newEvent.Id && e.SemanticVector is { Length: > 0 }).ToList();
        if (comparable.Count == 0)
        {
            return (null, null);
        }

        var maxSimilarity = comparable.Max(e => CosineSimilarity(vector, e.SemanticVector!));

        if (maxSimilarity < LowSimilarityThreshold)
        {
            return (maxSimilarity, $"Hint: \"{newEvent.Summary}\" reads as novel vs. recent events (similarity {maxSimilarity:F2}) — if it reveals new information, consider Important/Core on any related knowledge_update and whether it should advance a plot_thread.");
        }

        if (maxSimilarity > HighSimilarityThreshold)
        {
            return (maxSimilarity, $"Hint: \"{newEvent.Summary}\" closely echoes a recent event (similarity {maxSimilarity:F2}) — if this is reinforcement rather than new information, a Trivial-importance (or skipped) knowledge_update is probably enough.");
        }

        return (maxSimilarity, null);
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0;

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
