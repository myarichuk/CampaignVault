namespace CampaignVault.Plugins;

/// <summary>Additional RulesetData roots and campaign-option schema contributed by loaded plugin packages (set at host startup).</summary>
public static class PluginDataRoots
{
    public static IReadOnlyList<string> Additional { get; set; } = [];

    /// <summary>Flattened plugin-declared campaign option schema (for get_config / help surfaces).</summary>
    public static IReadOnlyList<PluginCampaignOption> DeclaredCampaignOptions { get; set; } = [];

    /// <summary>Mode IDs plugins declared <c>playerOnlyModeIds</c>: only the player switches them on or off.</summary>
    public static IReadOnlyCollection<string> PlayerOnlyModeIds { get; set; } = [];
}
