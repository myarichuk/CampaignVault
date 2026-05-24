namespace CampaignVault.Models;

public class SceneView
{
    public Location Location { get; set; } = default!;
    public IEnumerable<Character> PresentNPCs { get; set; } = [];
    public IEnumerable<RumorSummary> LocalRumors { get; set; } = [];
    public IEnumerable<Item> VisibleItems { get; set; } = [];
    public IEnumerable<Event> RecentEvents { get; set; } = [];
}

public class CommitResult
{
    public int ChangesProcessed { get; set; }
    public List<string> Summary { get; set; } = [];
}

public class AdvanceResult
{
    public CampaignTime NewTime { get; set; } = default!;
    public List<string> SimulatorEvents { get; set; } = [];
}
