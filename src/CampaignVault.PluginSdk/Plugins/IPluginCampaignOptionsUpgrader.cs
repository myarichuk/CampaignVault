namespace CampaignVault.Plugins;

/// <summary>
/// Lets a plugin migrate its own campaign options (plugin.json <c>campaignOptions</c>, stored in
/// <c>SystemOptions</c> on both the campaign meta document and <c>CampaignConfig</c>) when it renames, splits
/// or retires an option. The campaign-level counterpart of <see cref="IPluginTraitsUpgrader"/>, discovered the
/// same way (convention scanning, no registration).
///
/// The host runs every registered upgrader once at startup, over every campaign, as a data migration — not on
/// each load, since options are read from several places. Keep <see cref="TryUpgrade"/> idempotent: it runs
/// again on every restart, including over documents a previous run already upgraded.
/// </summary>
public interface IPluginCampaignOptionsUpgrader
{
    /// <summary>Must equal this plugin's <c>plugin.json</c> "id". Used for logging only.</summary>
    string PluginId { get; }

    /// <summary>
    /// Called with one campaign's live <c>SystemOptions</c> dictionary (once for the meta document's copy and once
    /// for the config's). Key comparison may be case-sensitive after a document round-trip, so match your own keys
    /// case-insensitively. Only touch keys your plugin declares, and never overwrite a key the player already set
    /// under the new name. Return <c>true</c> only if you changed something, so the host knows to save.
    /// A thrown exception is logged and skipped; it never blocks startup or other upgraders.
    /// </summary>
    bool TryUpgrade(IDictionary<string, string> systemOptions);
}
