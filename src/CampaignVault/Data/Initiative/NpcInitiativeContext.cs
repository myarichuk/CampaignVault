using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public sealed class NpcInitiativeContext
{
    public required Character Npc { get; init; }
    public Location? Location { get; init; }
    public IReadOnlyList<Character> PresentEntities { get; init; } = [];
    public IReadOnlyList<Event> RecentEvents { get; init; } = [];
    public required CampaignConfig Config { get; init; }
    public int CurrentDay { get; init; }
    public required string SurfacedViaTool { get; init; }
    public bool IncludeTensionBreakdown { get; init; }
}