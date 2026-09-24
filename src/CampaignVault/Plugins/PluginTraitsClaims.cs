namespace CampaignVault.Plugins;

/// <summary>
/// Every trait-key prefix ("&lt;pluginId&gt;." or "&lt;modeId&gt;.") a currently loaded plugin can claim,
/// set once at host startup (see <c>CampaignVaultModule.Load</c>). Read by
/// <c>PluginTraitsUpgradeRunner.WarnOnOrphanedTraitPrefixesAsync</c> to tell an orphaned prefix (no
/// plugin claims it) from a live one. Same set-once-at-startup idiom as <see cref="PluginDataRoots"/>.
/// </summary>
public static class PluginTraitsClaims
{
    public static IReadOnlyCollection<string> Claimed { get; set; } = [];
}
