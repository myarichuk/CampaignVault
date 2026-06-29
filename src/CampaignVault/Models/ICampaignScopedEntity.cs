namespace CampaignVault.Models;

public interface ICampaignScopedEntity : IHasSemanticVector
{
    string Id { get; set; }
    string? CampaignName { get; set; }
}