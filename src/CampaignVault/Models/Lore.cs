namespace CampaignVault.Models;

public class Lore
{
    public string Id { get; set; } = default!;
    
    public string Title { get; set; } = default!;
    
    public string Content { get; set; } = default!;
    
    public List<string> Tags { get; set; } = [];
    
    public List<string> Keywords { get; set; } = [];
    
    public string? Category { get; set; }
    
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
