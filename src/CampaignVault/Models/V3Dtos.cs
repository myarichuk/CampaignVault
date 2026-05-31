namespace CampaignVault.Models;

public class ToolResult<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public string[]? WorldPressure { get; set; }

    public ToolResult() { }
    public ToolResult(bool Success, T? Data = default, string? Summary = null, string? Error = null, string[]? WorldPressure = null)
    {
        this.Success = Success;
        this.Data = Data;
        this.Summary = Summary;
        this.Error = Error;
        this.WorldPressure = WorldPressure;
    }
}

public class WorldStateView
{
    public CampaignTime Time { get; set; } = default!;
    public IEnumerable<RumorSummary> ActiveRumors { get; set; } = [];
    public IEnumerable<Event> RecentEvents { get; set; } = [];
    public LocationSummary? PartyLocation { get; set; }
    public IEnumerable<string> WorldPressure { get; set; } = [];

    public WorldStateView() { }
    public WorldStateView(CampaignTime time, IEnumerable<RumorSummary> rumors, IEnumerable<Event> events, LocationSummary? location = null, IEnumerable<string>? pressure = null)
    {
        Time = time;
        ActiveRumors = rumors;
        RecentEvents = events;
        PartyLocation = location;
        WorldPressure = pressure ?? [];
    }
}
