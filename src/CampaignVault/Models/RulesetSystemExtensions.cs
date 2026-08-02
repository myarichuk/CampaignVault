namespace CampaignVault.Models;

/// <summary>
/// RulesetSystem constants are already in slug form (e.g. "dnd5e", "pf2e", "narrative").
/// This extension is provided for backwards compatibility; it returns the input unchanged.
/// </summary>
public static class RulesetSystemExtensions
{
    /// <summary>
    /// Returns the input unchanged. RulesetSystem constants are already in canonical slug form.
    /// </summary>
    public static string ToSlug(this string systemId) => systemId;
}