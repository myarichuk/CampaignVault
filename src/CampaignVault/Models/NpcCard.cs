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
    /// <summary>Mood as of this card (not part of the hash: mood changes travel in npcs[] and rosters).</summary>
    string? Mood = null,
    string? Traits = null,
    /// <summary>SystemExtension.Traits entries, filtered by the "&lt;modeId&gt;.&lt;name&gt;" convention:
    /// unprefixed keys (or keys whose prefix isn't a currently-enabled mode) always ride; a prefixed key
    /// rides only while this NPC is an active participant in that mode's encounter. Kept token-cheap —
    /// most turns carry no active mode, so this is usually null.</summary>
    string? SystemTraits = null,
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
        // Stance moves on ordinary beats (a +2 after a chat) and a tier crossing has its own context line
        // (RelationshipTierContextContributor), so it stays out; so do item counts (an arrow shot). A new
        // or lost item is news. The card's display keeps the numbers.
        var gearNames = Gear == null ? null : System.Text.RegularExpressions.Regex.Replace(Gear, @" x\d+", "");
        var text = string.Join("|", Traits, SystemTraits, Wants, Fears, Notes, Appearance,
            Stats?.ArmorClass, Stats?.Level, gearNames,
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
    /// <param name="modeParticipants">Every campaign-enabled mode id mapped to the character IDs currently
    /// active in that mode's encounter (empty set if the mode is enabled but has no active encounter).
    /// Gates SystemExtension.Traits entries prefixed "&lt;modeId&gt;.&lt;name&gt;": such a key rides only
    /// while this NPC is in the participant set for that modeId. A prefix that isn't a key here — because
    /// modeParticipants is null/empty, or because that mode isn't currently enabled, or because the prefix
    /// was never a mode id at all (e.g. "anatomy.cock") — is not gated data and always rides.</param>
    public static NpcCard Build(
        Character npc,
        IReadOnlyList<Character> party,
        IReadOnlyList<Item> heldItems,
        IReadOnlyList<MemoryNode>? preferredMemories = null,
        IReadOnlyDictionary<string, HashSet<string>>? modeParticipants = null)
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

        // Memories the beat is about (preferredMemories), else only defining ones (Core or urgent): routine
        // memories come when their topic does (MemoryRecallContextContributor), not on sight.
        var memories = (preferredMemories is { Count: > 0 }
                ? preferredMemories
                : (psych.Memories ?? []).Values
                    .Where(m => m.Importance == MemoryImportance.Core || m.Urgency >= MemoryUrgency.High)
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
            Mood: string.IsNullOrWhiteSpace(psych.CurrentMood) ? null : psych.CurrentMood,
            Traits: Join(psych.Traits),
            Wants: Join(psych.Wants),
            Fears: Join(psych.Fears),
            Stance: stance.Count > 0 ? string.Join("; ", stance) : null,
            Notes: string.IsNullOrWhiteSpace(notes) ? null : notes,
            Appearance: string.IsNullOrWhiteSpace(npc.CurrentAppearance) ? null : npc.CurrentAppearance,
            SystemTraits: JoinSystemTraits(npc, modeParticipants),
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

    /// <summary>Joins the SystemExtension.Traits entries visible to this NPC right now. A key follows the
    /// plugin namespacing convention "&lt;modeId&gt;.&lt;name&gt;" (e.g. "crafting.tool_quality"); a dotted
    /// key is mode-gated and rides only while this NPC is an active participant in that mode's encounter.
    /// An unprefixed key (no plugin claims it) always rides.</summary>
    private static string? JoinSystemTraits(Character npc, IReadOnlyDictionary<string, HashSet<string>>? modeParticipants)
    {
        var traits = npc.SystemStats?.Traits;
        if (traits is not { Count: > 0 })
        {
            return null;
        }

        var visible = traits
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && IsSystemTraitVisible(kv.Key, npc.Id, modeParticipants))
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();

        return visible.Count > 0 ? string.Join(", ", visible) : null;
    }

    private static bool IsSystemTraitVisible(string key, string npcId, IReadOnlyDictionary<string, HashSet<string>>? modeParticipants)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0)
        {
            return true; // unprefixed: no plugin mode claims this key, so it's a core trait
        }

        var modeId = key[..dot];
        if (modeParticipants == null || !modeParticipants.TryGetValue(modeId, out var participants))
        {
            return true; // the prefix isn't a currently-enabled mode, so it's not gated data either
        }

        return participants.Contains(npcId);
    }
}
