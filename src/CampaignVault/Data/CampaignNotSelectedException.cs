namespace CampaignVault.Data;

public sealed class CampaignNotSelectedException : Exception
{
    public CampaignNotSelectedException()
        : base("campaignName is required on every tool call (e.g. 'dragon-heist').")
    {
    }
}