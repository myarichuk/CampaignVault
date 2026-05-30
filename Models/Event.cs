using System.Text.Json.Serialization;

namespace CampaignVault.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventCategory
{
    Unresolved,
    Combat,
    Conversation,
    Discovery,
    Arrival,
    Betrayal,
    SceneCommit,
    Timeskip,
    Simulation,
    Interaction,
    Test
}

public class Event
{
    public string Id { get; set; } = default!;
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int DayLogged { get; set; }
    
    public string? SessionId { get; set; }
    
    public EventCategory Category { get; set; } = EventCategory.Unresolved;
    
    public string Summary { get; set; } = default!;
    
    public IDictionary<string, object>? Details { get; set; }
    
    public List<string> Involved { get; set; } = [];
}
