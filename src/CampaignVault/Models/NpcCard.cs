using System.Security.Cryptography;
using System.Text;
using CampaignVault.Data;
using CampaignVault.Data.Scenes;
using CampaignVault.Rulesets;

namespace CampaignVault.Models;

/// <summary>
/// T6 need-driven responses: what the DM needs to play an NPC, sent once per session (take_turn tracks
/// delivery on the TurnCursor) instead of re-riding every scene. Scene rosters carry only id, name,
/// activity and mood; this carries the rest. Sent on arrival for spotlight NPCs (plot/quest-linked,
/// active initiative, a relationship with the party, keepAlive) and on the first commit that involves
/// anyone else. get_entity / fullDetailCharacterId still return everything.
/// </summary>
public record NpcCard(
    string Id,
    string Name,
    string? Traits = null,
    string? Wants = null,
    string? Fears = null,
    /// <summary>How the NPC regards each party member they have an opinion of, e.g. "Tamsin: friendly (65)".</summary>
    string? Stance = null,
    string? Notes = null,
    string? Appearance = null,
    NpcStatLine? Stats = null,
    /// <summary>Held items by name; equipped ones marked with *.</summary>
    string? Gear = null,
    /// <summary>Only needs at or above <see cref="NpcCardFactory.PressingNeedThreshold"/>.</summary>
    Dictionary<string, float>? PressingNeeds = null,
    /// <summary>What this NPC's own (non-default) needs mean, e.g. "boredom": "restless without a task".</summary>
    Dictionary<string, string>? NeedNotes = null,
    /// <summary>At most two memories, "topic: details".</summary>
    List<string>? Memories = null)
{
    /// <summary>Hash of the stable fields only. Mood, activity, needs and memories change turn to turn and
    /// travel elsewhere (roster, context lines), so they must not force a resend.</summary>
    public string StableHash()
    {
        var text = string.Join("|", Traits, Wants, Fears, Stance, Notes, Appearance,
            Stats?.ArmorClass, Stats?.Level, Gear,
            NeedNotes == null ? null : string.Join(",", NeedNotes.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }
}

internal static class NpcCardFactory
{
    internal const float PressingNeedThreshold = 60f;
    private const int NotesCap = 300;
    private const int MemoryCap = 140;

    /// <param name="npc">The NPC.</param>
    /// <param name="party">Party members (for stance and names).</param>
    /// <param name="heldItems">Items the NPC holds.</param>
    /// <param name="preferredMemories">Memories already ranked relevant to this moment; falls back to salience.</param>
    public static NpcCard Build(
        Character npc,
        IReadOnlyList<Character> party,
        IReadOnlyList<Item> heldItems,
        IReadOnlyList<MemoryNode>? preferredMemories = null)
    {
        var psych = npc.Psychology ?? new PsychologyProfile();

        var stance = party
            .Where(p => npc.Social?.Relationships?.TryGetValue(p.Id, out var score) == true && score != 0)
            .Select(p =>
            {
                var score = npc.Social!.Relationships[p.Id];
                return $"{p.Name}: {RelationshipModifierHelper.TierName(score)} ({score})";
            })
            .ToList();

        var gear = heldItems
            .OrderByDescending(i => i.IsEquipped)
            .Select(i => (i.IsEquipped ? "*" : "") + i.Name + (i.Quantity > 1 ? $" x{i.Quantity}" : ""))
            .ToList();

        var pressing = (npc.Needs?.ActiveNeeds ?? [])
            .Where(kv => kv.Value >= PressingNeedThreshold)
            .ToDictionary(kv => kv.Key, kv => (float)Math.Round(kv.Value));

        var needNotes = (npc.Needs?.NeedDescriptors ?? [])
            .Where(kv => !(SceneNpcPresenceFactory.DefaultNeedDescriptors.TryGetValue(kv.Key, out var builtIn) && builtIn == kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var memories = (preferredMemories is { Count: > 0 }
                ? preferredMemories
                : (psych.Memories ?? []).Values
                    .OrderByDescending(m => m.Importance)
                    .ThenByDescending(m => m.Salience)
                    .ToList())
            .Take(2)
            .Select(Line)
            .ToList();

        var notes = npc.Notes;
        if (notes is { Length: > NotesCap })
        {
            notes = TextTruncation.TruncateAtBoundary(notes, NotesCap).Text + " (get_entity for more)";
        }

        return new NpcCard(
            npc.Id,
            npc.Name,
            Traits: Join(psych.Traits),
            Wants: Join(psych.Wants),
            Fears: Join(psych.Fears),
            Stance: stance.Count > 0 ? string.Join("; ", stance) : null,
            Notes: string.IsNullOrWhiteSpace(notes) ? null : notes,
            Appearance: string.IsNullOrWhiteSpace(npc.CurrentAppearance) ? null : npc.CurrentAppearance,
            Stats: NpcStatLine.From(npc.SystemStats),
            Gear: gear.Count > 0 ? string.Join(", ", gear) : null,
            PressingNeeds: pressing.Count > 0 ? pressing : null,
            NeedNotes: needNotes.Count > 0 ? needNotes : null,
            Memories: memories.Count > 0 ? memories : null);
    }

    /// <summary>"topic: details", capped; the wire form of a memory everywhere T6 pushes one.</summary>
    internal static string Line(MemoryNode m)
    {
        var text = $"{m.Topic}: {m.Details}";
        return text.Length <= MemoryCap ? text : TextTruncation.TruncateAtBoundary(text, MemoryCap).Text + "…";
    }

    private static string? Join(List<string>? values) =>
        values is { Count: > 0 } ? string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v))) : null;
}
