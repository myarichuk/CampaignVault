using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class NpcInitiativeContext
{
    public required Character Npc { get; init; }
    public Location? Location { get; init; }
    public IReadOnlyList<Character> PresentEntities { get; init; } = [];
    public IReadOnlyList<Event> RecentEvents { get; init; } = [];
    public IReadOnlyList<Event> NpcRecentEvents { get; init; } = [];
    public IReadOnlyList<Item> NpcHeldItems { get; init; } = [];
    public required CampaignConfig Config { get; init; }
    public int CurrentDay { get; init; }
    public required string SurfacedViaTool { get; init; }
    public bool IncludeTensionBreakdown { get; init; }

    /// <summary>
    /// Embedding of this turn's just-committed narrative/event text, when available.
    /// Null for query-only paths (e.g. get_scene) with no "just happened" text to embed.
    /// </summary>
    public float[]? TriggerVector { get; init; }
}