using System.Reflection;
using CampaignVault.Plugins;

namespace CampaignVault.Data.Templates;

/// <summary>
/// One kind of ruleset template (races, feats, ...) for every system, layered: the host's own data first (embedded
/// defaults plus the primary disk folder), then each plugin root in plugin load order (<see cref="PluginDataRoots.Additional"/>,
/// sorted by plugin id). A same-named template in a later layer replaces the earlier one and the replacement is logged
/// with both sources. <c>patches:</c> files merge into their target after every layer has loaded, in the same order;
/// one whose target doesn't exist is skipped with a warning. Then inheritance and list edits resolve.
/// </summary>
internal sealed class RulesetContentLayers<T> where T : RulesetTemplate
{
    private const string HostSource = "core";

    private readonly Dictionary<string, List<(RulesetTemplateLoader<T> Loader, string Source)>> _layers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<T, T, T> _merge;
    private readonly ILogger? _logger;

    public RulesetContentLayers(
        string rulesetDataDirectory,
        Assembly embeddedAssembly,
        string[] subfolderCandidates,
        Func<T, T, T> merge,
        ILogger? logger,
        IReadOnlyList<string>? pluginRoots = null)
    {
        _merge = merge;
        _logger = logger;

        pluginRoots ??= PluginDataRoots.Additional;
        int Order(string root) => IsSameRoot(root, rulesetDataDirectory)
            ? -1
            : IndexOf(pluginRoots, root);

        var discovered = RulesetDataSystemDiscovery
            .Discover(rulesetDataDirectory, embeddedAssembly, subfolderCandidates, pluginRoots)
            .OrderBy(d => d.systemSlug, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => Order(d.diskRoot));

        foreach (var (systemSlug, subfolder, diskRoot) in discovered)
        {
            if (!_layers.TryGetValue(systemSlug, out var list))
            {
                list = [];
                _layers[systemSlug] = list;
            }

            // Only the host layer extracts embedded defaults; a plugin root is a disk-only overlay.
            var isHost = IsSameRoot(diskRoot, rulesetDataDirectory);
            var embeddedPrefix = isHost
                ? $"CampaignVault.RulesetData.{systemSlug}.{subfolder}"
                : $"CampaignVault.RulesetData.__plugin__.{systemSlug}.{subfolder}";

            list.Add((
                new RulesetTemplateLoader<T>(Path.Combine(diskRoot, systemSlug, subfolder), embeddedAssembly, embeddedPrefix, logger),
                isHost ? HostSource : SourceLabel(diskRoot)));
        }
    }

    /// <summary>Every system that has a folder of this kind in some layer.</summary>
    public IReadOnlyCollection<string> Systems => _layers.Keys;

    /// <summary>Every template of this kind for <paramref name="system"/>, layered, patched and resolved. Empty for an unknown system.</summary>
    public IReadOnlyDictionary<string, T> Resolve(string system) => Resolve(system, []);

    /// <summary>
    /// Like <see cref="Resolve(string)"/>, with a campaign's homebrew as the last layer: YAML texts, each one template or
    /// patch, read the way a plugin file is. A text that doesn't parse is skipped with a warning (it was checked when saved).
    /// </summary>
    public IReadOnlyDictionary<string, T> Resolve(string system, IReadOnlyList<string> campaignYaml)
    {
        _layers.TryGetValue(system, out var layers);
        layers ??= [];
        if (layers.Count == 0 && campaignYaml.Count == 0)
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        var layerList = new List<(IReadOnlyDictionary<string, T> Templates, IReadOnlyList<T> Patches, string Source)>();
        foreach (var (loader, source) in layers)
        {
            var (templates, layerPatches) = loader.LoadLayer();
            layerList.Add((templates, layerPatches, source));
        }

        if (campaignYaml.Count > 0)
        {
            var parser = new RulesetTemplateLoader<T>(string.Empty, typeof(T).Assembly, "campaign", _logger);
            var homebrew = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var homebrewPatches = new List<T>();
            foreach (var yaml in campaignYaml)
            {
                try
                {
                    var template = parser.Parse(yaml, "campaign homebrew");
                    if (template?.PatchTarget != null)
                        homebrewPatches.Add(template);
                    else if (template?.Name != null)
                        homebrew[template.Name] = template;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "A campaign {Kind} homebrew template didn't parse; skipped.", typeof(T).Name);
                }
            }

            layerList.Add((homebrew, homebrewPatches, "campaign"));
        }

        var raw = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var patches = new List<T>();
        foreach (var (templates, layerPatches, source) in layerList)
        {
            foreach (var (name, template) in templates)
            {
                if (raw.TryGetValue(name, out var earlier) && earlier.Source != source)
                {
                    _logger?.LogWarning(
                        "{Kind} '{Name}' ({System}) from {Source} replaces the one from {Earlier} (last loaded wins). Use 'patches: {Name}' to change it instead.",
                        typeof(T).Name, name, system, source, earlier.Source, name);
                }

                template.Source = source;
                raw[name] = template;
            }

            foreach (var patch in layerPatches)
            {
                patch.Source = source;
                patches.Add(patch);
            }
        }

        foreach (var patch in patches)
        {
            if (!raw.TryGetValue(patch.PatchTarget!, out var target))
            {
                _logger?.LogWarning(
                    "{Kind} patch from {Source} targets '{Target}' ({System}), which doesn't exist; the patch is skipped.",
                    typeof(T).Name, patch.Source, patch.PatchTarget, system);
                continue;
            }

            raw[patch.PatchTarget!] = TemplateEdits.ApplyPatch(target, patch, _merge);
        }

        var resolver = new RulesetTemplateResolver<T>(name => raw.GetValueOrDefault(name), _merge);
        var resolved = resolver.ResolveAll(raw, _logger);
        // Hidden after inheritance resolved, so a child of a hidden template still finds its parent.
        return resolved.Values.Any(t => t.Hidden)
            ? resolved.Where(kv => !kv.Value.Hidden).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : resolved;
    }

    private static string SourceLabel(string root) =>
        PluginDataRoots.RootOwners.TryGetValue(root, out var pluginId)
            ? $"plugin '{pluginId}'"
            : $"'{root}'";

    private static bool IsSameRoot(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(IReadOnlyList<string> roots, string root)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            if (IsSameRoot(roots[i], root))
                return i;
        }

        return int.MaxValue;
    }
}
