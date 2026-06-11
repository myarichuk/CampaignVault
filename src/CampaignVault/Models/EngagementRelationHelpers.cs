namespace CampaignVault.Models;

public static class EngagementRelationHelpers
{
    public static string? ResolveVerb(string? verb, string? legacyRelationType) =>
        !string.IsNullOrWhiteSpace(verb) ? verb : legacyRelationType;

    public static bool IsClearRequest(string? verb, string? legacyRelationType) =>
        string.IsNullOrWhiteSpace(verb) && string.IsNullOrWhiteSpace(legacyRelationType);
}