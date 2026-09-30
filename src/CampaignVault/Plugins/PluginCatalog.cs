namespace CampaignVault.Plugins;

/// <summary>
/// One plugin package the host found at startup, loaded or not: what GET /plugins reports so a client's plugin
/// manager can show it without an MCP call (the model never sees this).
/// </summary>
/// <param name="Kind">"code" (ships a .dll) or "data" (plugin.json + RulesetData only).</param>
/// <param name="Source">"bundled" (the server's own Plugins folder) or "user" (a CAMPAIGN_PLUGIN_DIRS folder).</param>
/// <param name="Enabled">False when its id is in CAMPAIGN_PLUGINS_DISABLED.</param>
/// <param name="Loaded">True when it is active in this run (enabled, compatible, no id clash, loaded without error).</param>
public sealed record PluginStatus(
    string Id,
    string Name,
    string Version,
    string? Author,
    string? Description,
    string Kind,
    string Source,
    bool Enabled,
    bool Loaded,
    string Directory,
    string? MinEngineVersion,
    IReadOnlyList<string> Systems,
    IReadOnlyList<string> ModeIds,
    IReadOnlyList<PluginCampaignOption> CampaignOptions,
    IReadOnlyList<string> Errors);

/// <summary>Every plugin package found at startup (set once by <c>CampaignVaultModule.Load</c>).</summary>
public static class PluginCatalog
{
    public static IReadOnlyList<PluginStatus> Entries { get; set; } = [];

    /// <summary>The folders scanned, bundled first (for GET /plugins).</summary>
    public static IReadOnlyList<string> BundledDirectories { get; set; } = [];

    public static IReadOnlyList<string> UserDirectories { get; set; } = [];

    /// <summary>Env var: extra plugin folders (user-installed plugins), separated by the OS path separator.</summary>
    public const string DirsVariable = "CAMPAIGN_PLUGIN_DIRS";

    /// <summary>Env var: plugin ids not to load, separated by commas, semicolons or whitespace.</summary>
    public const string DisabledVariable = "CAMPAIGN_PLUGINS_DISABLED";

    public static IReadOnlyList<string> ParseDirectories(string? value) =>
    [
        .. (value ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ];

    public static IReadOnlySet<string> ParseDisabled(string? value) =>
        (value ?? "")
            .Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
