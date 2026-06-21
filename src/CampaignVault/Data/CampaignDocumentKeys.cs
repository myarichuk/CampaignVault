namespace CampaignVault.Data;

/// <summary>
/// Central provider for all document IDs in the CampaignVault storage model.
/// 
/// With the introduction of first-class <see cref="Models.Campaign"/>, all persistent
/// singletons are now namespaced under <c>campaigns/{name}/...</c>.
/// 
/// This service is the single source of truth for key construction. It enables:
/// - Clean multi-campaign support in the future
/// - "Lock in" of campaign type (each campaign has its own config + combat state)
/// - Easy migration / tooling around namespaced data
/// 
/// All repository methods, tools, and handlers should eventually route ID construction
/// through this service instead of hardcoding strings like "campaign/config" or "combat/current".
/// </summary>
public sealed class CampaignDocumentKeys
{
    /// <summary>
    /// Returns the document ID for the Campaign meta document itself.
    /// Example: "campaigns/dragonheist/meta"
    /// </summary>
    public string Meta(string campaignName) =>
        $"campaigns/{Normalize(campaignName)}/meta";

    /// <summary>
    /// Returns the document ID for a campaign's configuration (ruleset system + options).
    /// Replaces the old hardcoded "campaign/config".
    /// </summary>
    public string Config(string campaignName) =>
        $"campaigns/{Normalize(campaignName)}/config";

    /// <summary>
    /// Returns the document ID for the active combat encounter within a campaign.
    /// Replaces the old hardcoded "combat/current".
    /// </summary>
    public string CombatCurrent(string campaignName) =>
        $"campaigns/{Normalize(campaignName)}/combat/current";

    /// <summary>
    /// Returns the document ID for the campaign's world time state.
    /// Replaces the old hardcoded "state/time".
    /// </summary>
    public string StateTime(string campaignName) =>
        $"campaigns/{Normalize(campaignName)}/state/time";

    /// <summary>
    /// Returns the document ID for the campaign-specific need descriptors configuration.
    /// Replaces the old hardcoded "config/need-descriptors".
    /// </summary>
    public string NeedDescriptors(string campaignName) =>
        $"campaigns/{Normalize(campaignName)}/config/need-descriptors";

    /// <summary>
    /// Normalizes a campaign name into a safe, lowercase, hyphenated slug for use in document IDs.
    /// </summary>
    private static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentNullException(name);
        }

        // Very lightweight normalization — real validation can live in the create/select tools later.
        // Note: *very* inefficient, refactor at first opportunity 
        return name.Trim().ToLowerInvariant()
                   .Replace(' ', '-')
                   .Replace('_', '-')
                   .Replace('/', '-')
                   .Replace('\\', '-');
    }
}
