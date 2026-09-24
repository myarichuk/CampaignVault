namespace CampaignVault.Plugins;

/// <summary>
/// Lets a plugin migrate its own keys in <c>SystemExtension.Traits</c> when it changes its own trait
/// schema — a key rename, a value-shape change, or retiring a key. Discovered by convention scanning,
/// like <see cref="Guidance.IPluginGuidanceContributor"/> and <see cref="Context.IPluginContextContributor"/>.
/// The host runs every registered upgrader once per character on load, alongside the existing SystemStats
/// type coercion (<c>SystemStatsUpgradeHelper</c>).
///
/// Only load-order matters within a single plugin's own keys — the host does not sequence or version
/// upgraders across plugins, so keep <see cref="TryUpgrade"/> idempotent (safe to run on every load,
/// including a document your own prior run already upgraded).
/// </summary>
public interface IPluginTraitsUpgrader
{
    /// <summary>
    /// Must equal this plugin's <c>plugin.json</c> "id". The host uses it only for logging (which
    /// upgrader threw) and for matching against orphaned trait-key prefixes at startup — it does not
    /// gate which keys <see cref="TryUpgrade"/> is allowed to touch.
    /// </summary>
    string PluginId { get; }

    /// <summary>
    /// Called once per character load with that character's live <c>Traits</c> dictionary. Convention
    /// (not enforced by the host): only add, rename, or remove keys under your own
    /// <c>"&lt;pluginId|modeId&gt;."</c> prefix — see PLUGINS.md's "Key convention". Leave every other
    /// key alone so plugins never trample each other's data.
    ///
    /// Return <c>true</c> only if you changed something, so the host knows to persist the character;
    /// returning <c>false</c> on an already-current document avoids an unnecessary write on every load.
    /// A thrown exception is caught and logged by the host — it does not block character load or any
    /// other registered upgrader, but any partial mutation you made before throwing is not rolled back.
    /// </summary>
    bool TryUpgrade(IDictionary<string, string> traits);
}
