using System.ComponentModel;

namespace CampaignVault.Models;

/// <summary>
/// Response of start_session — a flat-size kickoff: the model-authored handoff from the last
/// end_session/checkpoint (or a capped server digest when there is none), campaign posture, time, open
/// quests, and a compact engine-truth party roster. Deliberately does NOT carry recent events, full
/// character sheets or the raw campaign document: those grew with every session played. The scene
/// itself comes from the first take_turn (fullDetailLocationId).
/// </summary>
public class SessionStartView
{
    [Description("The session number that is now open.")]
    public int SessionNumber { get; set; }

    [Description("Optional session title passed by the caller.")]
    public string? Title { get; set; }

    [Description("True when an already-open session was resumed (e.g. after a reconnect) instead of opening a new one.")]
    public bool Resumed { get; set; }

    [Description("Your handoff from the last end_session (or checkpoint): storySoFar, lastSession, openThreads, npcsInPlay, partyIntent, tone. Narrative only — trust party/engine fields below over it. Fold storySoFar forward when you next call end_session.")]
    public SessionHandoffView? Handoff { get; set; }

    [Description("Server-built digest of recent important events, present only when no handoff exists (older campaign, or end_session was never called).")]
    public string? RecentDigest { get; set; }

    [Description("Campaign posture: ruleset system, narrative focus, house-rule options, party roster, entry hint.")]
    public SessionCampaignView Campaign { get; set; } = null!;

    [Description("Current in-world date and time.")]
    public string Time { get; set; } = "";

    [Description("Open quests in scope (id, title, open objectives, deadline). Detail via get_entity.")]
    public List<SessionQuestView>? ActiveQuests { get; set; }

    [Description("Seed coverage counts + gaps; present only while the world still has seeding gaps (session 0/1 signal).")]
    public SeedCoverageSummary? SeedCoverage { get; set; }

    [Description("Party roster from the DB (authoritative HP, AC, location, conditions, gear, high needs, memory index). Full sheet: get_entity; all memories: take_turn memoriesOnlyCharacterId.")]
    public List<PartySessionView> Party { get; set; } = [];

    [Description("Echo as clientPartyFingerprint on your first take_turn.")]
    public string PartyFingerprint { get; set; } = "";
}

/// <summary>Campaign posture for start_session — the narrative-facing slice of the campaign meta document.
/// Engine bookkeeping on that document (pressure cooldowns, initiative-surfaced state, commit counters)
/// never goes on the wire.</summary>
public record SessionCampaignView(
    string Slug,
    string DisplayName,
    string System,
    bool IsSystemLocked,
    List<string>? NarrativeFocus,
    Dictionary<string, string>? SystemOptions,
    IReadOnlyList<PartyMemberSummary> Pcs,
    IReadOnlyList<PartyMemberSummary> Companions,
    CampaignEntryHint EntryHint)
{
    public static SessionCampaignView From(Campaign campaign, CampaignPosture posture) => new(
        posture.Slug,
        posture.DisplayName,
        posture.System,
        posture.IsSystemLocked,
        campaign.NarrativeFocus is { Count: > 0 } focus ? focus : null,
        campaign.SystemOptions is { Count: > 0 } options ? options : null,
        posture.Pcs,
        posture.Companions,
        posture.EntryHint);
}

public record SessionQuestView(string Id, string Title, int OpenObjectives, int? DeadlineDay, bool IsOverdue)
{
    public static SessionQuestView From(ActiveQuestSummary quest) =>
        new(quest.QuestId, quest.Title, quest.OpenObjectiveCount, quest.DeadlineDay, quest.IsOverdue);
}

/// <summary>
/// Compact party member for start_session: the engine facts a DM needs to resume (HP, AC, level,
/// location, conditions, gear names, needs that are actually pressing) plus a memory index — count and
/// the top topics by salience — instead of the full memory bodies, which grow every session.
/// </summary>
public record PartySessionView(
    string Id,
    string Name,
    bool IsPc,
    string Hp,
    int? Ac,
    string? ClassLevel,
    int? Level,
    string? LocationId,
    string? Activity,
    List<string>? Conditions,
    List<string>? Equipped,
    List<string>? Carried,
    Dictionary<string, int>? HighNeeds,
    int MemoryCount,
    List<string>? KeyMemories)
{
    /// <summary>Needs at or above this value are surfaced; lower ones are ambient and left to take_turn.</summary>
    public const float HighNeedThreshold = 60f;

    public const int KeyMemoryCount = 3;

    public static PartySessionView From(Character c, IReadOnlyList<SessionItemName> heldItems)
    {
        var (ac, level) = c.SystemStats switch
        {
            Dnd5eExtension d => ((int?)d.ArmorClass, d.Level),
            Pf2eExtension p => ((int?)p.ArmorClass, p.Level),
            _ => ((int?)null, (int?)null),
        };

        var conditions = c.SystemStats.StatusEffects.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var highNeeds = c.Needs.ActiveNeeds
            .Where(n => n.Value >= HighNeedThreshold)
            .OrderByDescending(n => n.Value)
            .ToDictionary(n => n.Key, n => (int)MathF.Round(n.Value));
        var memories = c.Psychology.Memories.Values;
        var keyMemories = memories
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.Salience)
            .ThenByDescending(m => m.DayAcquired)
            .Take(KeyMemoryCount)
            .Select(m => m.Topic)
            .ToList();
        var equipped = heldItems.Where(i => i.IsEquipped).Select(i => i.Name).ToList();
        var carried = heldItems.Where(i => !i.IsEquipped).Select(i => i.Name).ToList();

        return new PartySessionView(
            c.Id,
            c.Name,
            c.IsPc,
            $"{c.CurrentHp}/{c.MaxHp}",
            ac,
            c.ClassLevel,
            level,
            c.CurrentLocationId,
            c.CurrentActivity,
            conditions.Count > 0 ? conditions : null,
            equipped.Count > 0 ? equipped : null,
            carried.Count > 0 ? carried : null,
            highNeeds.Count > 0 ? highNeeds : null,
            memories.Count,
            keyMemories.Count > 0 ? keyMemories : null);
    }
}

/// <summary>Query-layer projection of a held item: start_session only needs the name and the slot.</summary>
public class SessionItemName
{
    public string HolderId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEquipped { get; set; }
}
