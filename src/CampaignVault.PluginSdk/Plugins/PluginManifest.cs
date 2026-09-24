using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampaignVault.Plugins;

/// <summary>
/// On-disk <c>plugin.json</c> for a nested plugin package under <c>Plugins/&lt;Name&gt;/</c>.
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("minEngineVersion")]
    public string? MinEngineVersion { get; set; }

    [JsonPropertyName("modeIds")]
    public List<string> ModeIds { get; set; } = [];

    [JsonPropertyName("rulesetDataRoots")]
    public List<string> RulesetDataRoots { get; set; } = [];

    [JsonPropertyName("skillsPath")]
    public string? SkillsPath { get; set; }

    /// <summary>
    /// Plugin-declared campaign option schema. Host merges keys into SystemOptions defaults / config help surfaces.
    /// Runtime values still live in <c>CampaignConfig.SystemOptions</c>.
    /// </summary>
    [JsonPropertyName("campaignOptions")]
    public List<PluginCampaignOption> CampaignOptions { get; set; } = [];

    /// <summary>
    /// Domain event topics this plugin publishes (each must start with "{id}."). Informational: the host uses
    /// it at startup to warn about subscriptions to topics nobody publishes (usually a typo or a missing plugin).
    /// </summary>
    [JsonPropertyName("publishes")]
    public List<string> Publishes { get; set; } = [];

    public static PluginManifest? TryLoad(string pluginJsonPath, out string? error)
    {
        error = null;
        if (!File.Exists(pluginJsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(pluginJsonPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                error = $"plugin.json at '{pluginJsonPath}' is missing required 'id'.";
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse plugin.json at '{pluginJsonPath}': {ex.Message}";
            return null;
        }
    }

    /// <summary>SemVer-ish compare: returns &lt;0 if a&lt;b, 0 if equal, &gt;0 if a&gt;b. Non-numeric segments compare ordinal.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static string[] Parts(string? v) =>
            string.IsNullOrWhiteSpace(v) ? ["0"] : v.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var pa = Parts(a);
        var pb = Parts(b);
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            var na = int.TryParse(sa, out var ia);
            var nb = int.TryParse(sb, out var ib);
            if (na && nb)
            {
                var cmp = ia.CompareTo(ib);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }
}

/// <summary>Schema entry for a plugin-declared campaign option (stored in SystemOptions at runtime).</summary>
public sealed class PluginCampaignOption
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>string | enum | bool | int</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("values")]
    public List<string>? Values { get; set; }

    [JsonPropertyName("default")]
    public string? Default { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
