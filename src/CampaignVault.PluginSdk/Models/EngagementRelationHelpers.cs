namespace CampaignVault.Models;

public static class EngagementRelationHelpers
{
    public static string? ResolveVerb(string? verb) =>
        !string.IsNullOrWhiteSpace(verb) ? verb : null;

    public static bool IsClearRequest(string? verb) =>
        string.IsNullOrWhiteSpace(verb);
}