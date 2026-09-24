using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// end_session's structured handoff — a compaction-style summary the next start_session returns in
/// place of the raw event/memory dump. Narrative only: HP, location, gear and conditions are never read
/// from here (start_session takes those from the DB). Caps are enforced by <see cref="SessionHandoffRules"/>.
/// </summary>
public class SessionHandoff
{
    [Description("Rolling story summary (≤800 chars). Fold the previous storySoFar from start_session together with this session; don't just append.")]
    [JsonPropertyName("storySoFar")]
    public string? StorySoFar { get; set; }

    [Description("What happened this session (≤600 chars). Required.")]
    [JsonPropertyName("lastSession")]
    public string? LastSession { get; set; }

    [Description("Unresolved hooks and questions, in your words (≤6 items, ≤120 chars each).")]
    [JsonPropertyName("openThreads")]
    public List<string>? OpenThreads { get; set; }

    [Description("NPCs in play and where they stand (≤8). Ids must exist in the campaign.")]
    [JsonPropertyName("npcsInPlay")]
    public List<NpcStance>? NpcsInPlay { get; set; }

    [Description("What the players said they'd do next (≤200 chars).")]
    [JsonPropertyName("partyIntent")]
    public string? PartyIntent { get; set; }

    [Description("Optional voice/tone note for continuity (≤120 chars).")]
    [JsonPropertyName("tone")]
    public string? Tone { get; set; }
}

public class NpcStance
{
    [Description("Character id, e.g. chars/oda.")]
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [Description("Their stance toward the party right now (≤80 chars).")]
    [JsonPropertyName("stance")]
    public string? Stance { get; set; }
}

/// <summary>The stored handoff as start_session returns it: which session wrote it, and whether it is a
/// mid-session checkpoint rather than an end-of-session summary.</summary>
public record SessionHandoffView(
    int FromSession,
    bool Checkpoint,
    string? StorySoFar,
    string? LastSession,
    List<string>? OpenThreads,
    List<NpcStance>? NpcsInPlay,
    string? PartyIntent,
    string? Tone)
{
    public static SessionHandoffView From(SessionLog.SessionRecord record) => new(
        record.Number,
        record.HandoffIsCheckpoint,
        record.Handoff!.StorySoFar,
        record.Handoff.LastSession,
        record.Handoff.OpenThreads is { Count: > 0 } threads ? threads : null,
        record.Handoff.NpcsInPlay is { Count: > 0 } npcs ? npcs : null,
        record.Handoff.PartyIntent,
        record.Handoff.Tone);
}

/// <summary>Length caps for <see cref="SessionHandoff"/>. Over-cap input is rejected with the overage per
/// field (never silently truncated) so the model rewrites it instead of losing its tail.</summary>
public static class SessionHandoffRules
{
    public const int StorySoFarMax = 800;
    public const int LastSessionMax = 600;
    public const int OpenThreadsMaxCount = 6;
    public const int OpenThreadMaxLength = 120;
    public const int NpcsInPlayMaxCount = 8;
    public const int StanceMaxLength = 80;
    public const int PartyIntentMax = 200;
    public const int ToneMax = 120;

    /// <summary>Trims whitespace and drops empty list entries in place, then returns one message per
    /// violated cap (empty when the handoff fits).</summary>
    public static List<string> NormalizeAndValidate(SessionHandoff handoff)
    {
        handoff.StorySoFar = Clean(handoff.StorySoFar);
        handoff.LastSession = Clean(handoff.LastSession);
        handoff.PartyIntent = Clean(handoff.PartyIntent);
        handoff.Tone = Clean(handoff.Tone);
        handoff.OpenThreads = handoff.OpenThreads?
            .Select(Clean).Where(t => t != null).Select(t => t!).ToList();
        handoff.NpcsInPlay = handoff.NpcsInPlay?
            .Where(n => !string.IsNullOrWhiteSpace(n.Id))
            .Select(n => new NpcStance { Id = n.Id.Trim(), Stance = Clean(n.Stance) })
            .ToList();

        var problems = new List<string>();
        if (handoff.LastSession == null)
        {
            problems.Add("lastSession is required (what happened this session).");
        }

        CheckLength(problems, "storySoFar", handoff.StorySoFar, StorySoFarMax);
        CheckLength(problems, "lastSession", handoff.LastSession, LastSessionMax);
        CheckLength(problems, "partyIntent", handoff.PartyIntent, PartyIntentMax);
        CheckLength(problems, "tone", handoff.Tone, ToneMax);

        if (handoff.OpenThreads is { } threads)
        {
            if (threads.Count > OpenThreadsMaxCount)
            {
                problems.Add($"openThreads has {threads.Count} items (max {OpenThreadsMaxCount}); merge or drop the least live ones.");
            }

            for (var i = 0; i < threads.Count; i++)
            {
                CheckLength(problems, $"openThreads[{i}]", threads[i], OpenThreadMaxLength);
            }
        }

        if (handoff.NpcsInPlay is { } npcs)
        {
            if (npcs.Count > NpcsInPlayMaxCount)
            {
                problems.Add($"npcsInPlay has {npcs.Count} entries (max {NpcsInPlayMaxCount}); keep the ones that matter next session.");
            }

            for (var i = 0; i < npcs.Count; i++)
            {
                CheckLength(problems, $"npcsInPlay[{i}].stance", npcs[i].Stance, StanceMaxLength);
            }

            var duplicates = npcs.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                problems.Add($"npcsInPlay lists {string.Join(", ", duplicates)} more than once.");
            }
        }

        return problems;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void CheckLength(List<string> problems, string field, string? value, int max)
    {
        if (value != null && value.Length > max)
        {
            problems.Add($"{field} is {value.Length} chars (max {max}, {value.Length - max} over).");
        }
    }
}
