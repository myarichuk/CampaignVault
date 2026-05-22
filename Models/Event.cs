using LiteDB;

namespace CampaignVault.Models;

public class Event
{
    [BsonId]
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public string? SessionId { get; set; }
    
    public string Type { get; set; } = default!;
    
    public string Summary { get; set; } = default!;
    
    public IDictionary<string, object>? Details { get; set; }
    
    public List<string> Involved { get; set; } = [];
}
