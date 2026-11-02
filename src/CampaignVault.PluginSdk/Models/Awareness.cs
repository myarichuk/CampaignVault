namespace CampaignVault.Models;

/// <summary>How much of their surroundings an observer is taking in (WITNESS_CHANNEL_PLAN.md A1).</summary>
public enum AwarenessState
{
    Alert,

    /// <summary>Absorbed in something: passive Perception −5 (the SRD's disadvantage value; house rule D7).</summary>
    Distracted,

    /// <summary>Can't see; passive Perception −5 (dnd5e house rule D6) or −4 (pf2e unconscious). Loud signals wake them.</summary>
    Asleep,
}

/// <summary>An observer's lasting awareness state. Null on the character means alert.</summary>
public class Awareness
{
    public AwarenessState State { get; set; }

    public string? Reason { get; set; }

    /// <summary>Campaign hour (TotalDaysElapsed * 24 + Hour) it was set.</summary>
    public double? SinceHour { get; set; }
}

/// <summary>
/// A source hiding from observers (A2): the Stealth total each observer's passive Perception must meet to notice it.
/// Cleared when the source attacks, makes noise, or is revealed (SRD 5.1 Hiding: "you give away your position if you make noise").
/// </summary>
public class Concealment
{
    public int StealthTotal { get; set; }

    public double? SinceHour { get; set; }
}

/// <summary>
/// An NPC on their way out (IMPRESSIONS_PLAN.md D9): still at <see cref="FromLocationId"/>, but already Distant from everyone there.
/// The engine completes the move after <see cref="DefaultBeats"/> more committed beats or <see cref="CompleteAfterHours"/> of campaign
/// time, whichever comes first.
/// </summary>
public class Departing
{
    /// <summary>Where they're headed; null leaves them nowhere in particular (off-screen).</summary>
    public string? ToLocationId { get; set; }

    public string? FromLocationId { get; set; }

    /// <summary>Campaign hour (TotalDaysElapsed * 24 + Hour) they set off.</summary>
    public double SinceHour { get; set; }

    /// <summary>Committed beats left before they're gone. Sub-hour beats don't move the clock, so beats count too.</summary>
    public int BeatsRemaining { get; set; } = DefaultBeats;

    public const double CompleteAfterHours = 0.25;

    public const int DefaultBeats = 2;
}
