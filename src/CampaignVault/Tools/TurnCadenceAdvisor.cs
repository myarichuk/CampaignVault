using System.Collections.Concurrent;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Advisory guard for "one player beat = one take_turn". The server can't see player messages, so it
/// judges by cadence: a commit landing seconds after the previous one is almost certainly the same beat.
/// Two follow-ups are legitimate: a roll's fallout (knowledge_update / event citing the roll) and the
/// save an engine-fired HAZARD asks for. Anything else in a back-to-back no-roll commit (activity,
/// initiative nudge, ...) is housekeeping that should ride the previous batch or wait for the next
/// player beat. Never blocks a commit: dropping state is worse than an extra call.
/// </summary>
internal static class TurnCadenceAdvisor
{
    /// <summary>A player reading a reply and typing a new one takes longer than this; an LLM's next tool call doesn't.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private const int Cap = 256;

    private sealed record LastCommit(DateTime At, bool HadRoll, bool HazardPending, int FollowUpsSinceRoll, int Chain = 1);

    /// <summary>Commits in a row, each inside Window of the last, at which even roll-carrying batches get flagged.</summary>
    internal const int ChainAdvisoryLength = 4; // roll + hazard save + knowledge_update is the longest legitimate chain (3)

    private static readonly ConcurrentDictionary<string, LastCommit> Last = new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearForTests() => Last.Clear();

    /// <summary>Returns a warning when this batch looks like a needless extra commit for the same beat.</summary>
    internal static string? Evaluate(string campaign, IReadOnlyList<WorldChange> changes, DateTime? now = null)
    {
        if (!Last.TryGetValue(campaign, out var prev) || (now ?? DateTime.UtcNow) - prev.At > Window)
        {
            return null;
        }

        if (prev.Chain + 1 >= ChainAdvisoryLength)
        {
            // Rolls are exempt one at a time, but a run of them is the model playing a chase or fight alone.
            return $"TURN CADENCE: commit #{prev.Chain + 1} in a row with no pause. One player message = one beat: stop " +
                   "chaining, narrate what has landed as a full scene, and let the player's next message decide what happens next.";
        }

        if (changes.Any(c => c is RulesetAction))
        {
            return null; // a new roll (e.g. the hazard save) is its own uncertain outcome
        }

        var extras = changes
            .Where(c => c is not (KnowledgeUpdate or EventOccurred))
            .Select(c => c.GetType().Name)
            .Distinct()
            .ToList();
        var followUpAllowed = (prev.HadRoll || prev.HazardPending) && prev.FollowUpsSinceRoll == 0;

        if (followUpAllowed && extras.Count == 0)
        {
            return null; // the approved split: roll, then commit what it revealed
        }

        var what = extras.Count > 0 ? $": {string.Join(", ", extras)}" : "";
        return $"TURN CADENCE: extra commit seconds after the last, no new roll{what}. One player beat = one take_turn " +
               "(split only for a hazard save or a knowledge_update citing the roll); fold NPC activity/nudges into the roll's batch or wait for the next player beat.";
    }

    /// <summary>Records a successful commit so the next call can be judged against it.</summary>
    internal static void Record(string campaign, IReadOnlyList<WorldChange> changes, IEnumerable<string>? summary, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var hadRoll = changes.Any(c => c is RulesetAction);
        var hazard = summary?.Any(s => s.Contains("HAZARD:", StringComparison.Ordinal)) == true;

        var followUps = 0;
        var chain = 1;
        if (Last.TryGetValue(campaign, out var prev) && at - prev.At <= Window)
        {
            chain = prev.Chain + 1;
            if (!hadRoll && (prev.HadRoll || prev.FollowUpsSinceRoll > 0))
            {
                followUps = prev.FollowUpsSinceRoll + 1;
            }
        }

        if (Last.Count > Cap)
        {
            foreach (var stale in Last.Where(kv => at - kv.Value.At > Window).Select(kv => kv.Key).ToList())
            {
                Last.TryRemove(stale, out _);
            }
        }

        Last[campaign] = new LastCommit(at, hadRoll, hazard, followUps, chain);
    }
}
