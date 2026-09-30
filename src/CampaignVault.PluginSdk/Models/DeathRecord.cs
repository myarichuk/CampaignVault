namespace CampaignVault.Models;

/// <summary>
/// Persistent record that a character has died. Set by the core <c>death</c> verb (<see cref="DeathChange"/>) and
/// cleared only by the same verb with <c>revive: true</c>. Null on every living character, so existing documents
/// load unchanged. A dead character keeps its document, memories and gear; the engine simply stops simulating it.
/// </summary>
public class DeathRecord
{
    /// <summary>In-world day of death (<c>TotalDaysElapsed</c>).</summary>
    public int Day { get; set; }

    /// <summary>What killed them, in a phrase ("goblin arrow", "starved in the gorse").</summary>
    public string? Cause { get; set; }

    /// <summary>Character id of the killer, when someone did it.</summary>
    public string? KillerId { get; set; }

    /// <summary>
    /// Where the body lies. The character's live <c>CurrentLocationId</c> is cleared on death so location queries
    /// stop listing them as present; the corpse (and its lootable gear) is found here instead.
    /// </summary>
    public string? BodyLocationId { get; set; }
}
