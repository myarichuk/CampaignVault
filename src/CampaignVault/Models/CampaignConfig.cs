using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Singleton campaign configuration document, stored in RavenDB as "campaign/config".
/// Controls which ruleset plugin is active and holds any house-rule overrides.
/// Loaded by RulesetActionHandler on every ruleset action dispatch.
/// </summary>
public class CampaignConfig
{
    /// <summary>Fixed RavenDB document ID. Do not change.</summary>
    public string Id { get; set; } = "campaign/config";

    /// <summary>
    /// The active TTRPG ruleset for this campaign.
    /// Determines which IRulesetResolver handles RulesetAction WorldChanges.
    /// Defaults to D&amp;D 5e.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RulesetSystem ActiveSystem { get; set; } = RulesetSystem.Dnd5e;

    /// <summary>
    /// Optional house-rule overrides passed to resolvers.
    /// Keys and values are resolver-specific. Examples:
    /// "lingeringInjuries" → "true" (D&amp;D 5e optional rule)
    /// "mapEnabled" → "true" (PF2e multi-attack penalty tracking)
    /// </summary>
    public Dictionary<string, string> SystemOptions { get; set; } = [];
}
