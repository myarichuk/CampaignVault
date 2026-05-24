namespace CampaignVault.Models;

public class Event
{
    public string Id { get; set; } = default!;
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public int DayLogged { get; set; }
    
    public string? SessionId { get; set; }
    
    public string Type { get; set; } = default!;
    
    public string Summary { get; set; } = default!;
    
    public IDictionary<string, object>? Details { get; set; }
    
    public List<string> Involved { get; set; } = [];
}
