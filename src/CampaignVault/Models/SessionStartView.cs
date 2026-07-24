using System.ComponentModel;

namespace CampaignVault.Models;

/// <summary>
/// Response of start_session — the single kickoff payload: session record + recap, campaign context,
/// authoritative world state (with seed coverage), and the party roster, all in one round-trip.
/// </summary>
public class SessionStartView
{
    [Description("The session number that is now open.")]
    public int SessionNumber { get; set; }

    [Description("Optional session title passed by the caller.")]
    public string? Title { get; set; }

    [Description("True when an already-open session was resumed (e.g. after a reconnect) instead of opening a new one.")]
    public bool Resumed { get; set; }

    [Description("Recap text of the most recent closed session, or a note that there are no prior sessions.")]
    public string LastSessionRecap { get; set; } = "";

    [Description("Campaign meta + posture: ruleset system, narrative focus, party roster hint, entry hint, last event.")]
    public CampaignContextView? Campaign { get; set; }

    [Description("Authoritative world state: time, scoped rumors/quests/factions, recent events, pressures, and seedCoverage (entity counts + gap hints — check right after world_build).")]
    public WorldStateView WorldState { get; set; } = default!;

    [Description("Active party roster (isPc or isPartyCompanion) with equipped/carried items.")]
    public List<PartyMemberView> Party { get; set; } = [];
}
