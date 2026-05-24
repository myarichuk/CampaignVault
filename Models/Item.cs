namespace CampaignVault.Models;

public class Item
{
    public string Id { get; set; } = default!;
    
    public string Name { get; set; } = default!;
    
    public string Description { get; set; } = default!;
    
    public string HolderId { get; set; } = default!; // Character.Id, Location.Id, or Item.Id
    
    public List<string> Tags { get; set; } = [];
    
    public Dictionary<string, object> Properties { get; set; } = [];
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
