using CampaignVault.Plugins;
using Raven.Embedded;

namespace CampaignVault.Hosting;

/// <summary>
/// Environment switches read once at startup, kept out of Program.cs so their precedence can be tested.
/// </summary>
public static class HostSwitches
{
    /// <summary>
    /// Tri-state flag: "1"/"true" forces on, "0"/"false" forces off, anything else (unset, blank) falls back to
    /// the environment default. MCP_BIND_ANY and HTTPS_ENABLED default to on outside Development; an explicit
    /// "0" is how a Production-environment embedded server (the Unity client's) stays loopback-only over HTTP.
    /// </summary>
    public static bool Resolve(string? value, bool fallback)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return fallback;
        }
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return fallback;
    }

    /// <summary>
    /// RavenDB license from CAMPAIGN_RAVEN_LICENSE (the license JSON itself) or CAMPAIGN_RAVEN_LICENSE_PATH (a file
    /// holding it); the inline value wins when both are set. Null when neither is set: RavenDB then runs unlicensed
    /// (AGPLv3 terms, see COMMERCIAL.md). The EULA is accepted either way, as RavenDB.Embedded requires to start.
    /// </summary>
    public static ServerOptions.LicensingOptions? RavenLicensing(string? license, string? licensePath)
    {
        if (!string.IsNullOrWhiteSpace(license))
        {
            return new ServerOptions.LicensingOptions { License = license.Trim(), EulaAccepted = true };
        }
        if (!string.IsNullOrWhiteSpace(licensePath))
        {
            return new ServerOptions.LicensingOptions { LicensePath = licensePath.Trim(), EulaAccepted = true };
        }
        return null;
    }

    /// <summary>What the startup banner says about the license; never prints the license itself.</summary>
    public static string DescribeLicensing(ServerOptions.LicensingOptions? licensing)
    {
        if (licensing is null)
        {
            return "none (set CAMPAIGN_RAVEN_LICENSE or CAMPAIGN_RAVEN_LICENSE_PATH; see COMMERCIAL.md)";
        }
        return licensing.License is not null ? "from CAMPAIGN_RAVEN_LICENSE" : $"from file {licensing.LicensePath}";
    }

    /// <summary>Startup banner summary of the plugin catalog, e.g. "6 loaded (1 user), 1 disabled, 1 with errors".</summary>
    public static string DescribePlugins(IReadOnlyList<PluginStatus> plugins)
    {
        var loaded = plugins.Count(p => p.Loaded);
        var user = plugins.Count(p => p.Loaded && p.Source == "user");
        var disabled = plugins.Count(p => !p.Enabled);
        var failed = plugins.Count(p => p.Errors.Count > 0);
        var text = $"{loaded} loaded" + (user > 0 ? $" ({user} user)" : "");
        if (disabled > 0) text += $", {disabled} disabled";
        if (failed > 0) text += $", {failed} with errors";
        return text;
    }
}
