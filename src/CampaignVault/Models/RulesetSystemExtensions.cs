namespace CampaignVault.Models;

/// <summary>
/// Canonical slug strings for ruleset systems, used in resource pool schemas and campaign config.
/// </summary>
public static class RulesetSystemExtensions
{
    public static string ToSlug(this RulesetSystem system) => system switch
    {
        RulesetSystem.Dnd5e => "dnd5e",
        RulesetSystem.Pathfinder2e => "pf2e",
        RulesetSystem.Narrative => "narrative",
        _ => system.ToString().ToLowerInvariant()
    };
}