using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

public sealed class SceneNpcPresenceContext
{
    public required IReadOnlyList<Character> PresentNpcs { get; init; }
    public required Location Location { get; init; }
    public required IReadOnlyList<Event> RecentSceneEvents { get; init; }
    public required IReadOnlyList<Event> RecentCampaignEvents { get; init; }
    public required IReadOnlyDictionary<string, List<Item>> ItemsByHolder { get; init; }
    public required IReadOnlyDictionary<string, string> GlobalNeedDescriptors { get; init; }
    public required CampaignTime Time { get; init; }
    public required CampaignConfig Config { get; init; }
    public required Campaign Campaign { get; init; }
}
