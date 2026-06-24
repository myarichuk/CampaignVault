namespace CampaignVault.Data;

/// <summary>
/// Central provider for all document IDs in the CampaignVault storage model.
/// 
/// With the introduction of first-class <see cref="Models.Campaign"/>, all persistent
/// singletons are now namespaced under <c>campaigns/{name}/...</c>.
/// 
/// This service is the single source of truth for key construction. It enables:
/// - Per-campaign namespacing of singletons (meta, config, time, combat)
/// - Ruleset lock-in per campaign (each slug has its own config + combat state)
/// - Consistent ID construction for repository, tools, and handlers
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
        $"campaigns/{CampaignSlug.Canonicalize(campaignName)}/config/need-descriptors";

    private static string Normalize(string name) => CampaignSlug.Canonicalize(name);
}
