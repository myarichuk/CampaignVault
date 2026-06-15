using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

internal sealed class SceneAssemblyContext
{
    public required string RequestedLocationId { get; init; }
    public required string EffectiveCampaign { get; init; }
    public required Location Location { get; init; }
    public required IReadOnlyList<Character> NpcsFromIndex { get; init; }
    public required IReadOnlyList<Character> NpcsFromSimulation { get; init; }
    public required IReadOnlyList<Rumor> Rumors { get; init; }
    public required IReadOnlyList<Item> Items { get; init; }
    public required IReadOnlyList<Event> Events { get; init; }
    public required CampaignTime Time { get; init; }
    public required IReadOnlyDictionary<string, string> GlobalNeedDescriptors { get; init; }
    public required CampaignConfig Config { get; init; }
    public required Campaign Campaign { get; init; }
    public required IReadOnlyList<Event> RecentCampaignEvents { get; init; }
    public required IReadOnlyDictionary<string, List<Item>> ItemsByHolder { get; init; }
    public CombatEncounter? ActiveCombat { get; init; }
    public required IReadOnlyList<Quest> ActiveQuests { get; init; }
    public required IReadOnlyList<Faction> RelevantFactions { get; init; }
    public bool MarkVisited { get; init; }
}
