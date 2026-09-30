using System.Reflection;
using System.Runtime.Loader;
using CampaignVault.Plugins;

namespace CampaignVault.AutofacModules;

/// <summary>
/// Loads plugin assemblies from flat <c>Plugins/*.dll</c> and nested <c>Plugins/*/*.dll</c> packages.
/// Unifies <c>CampaignVault.PluginSdk</c> types onto <see cref="AssemblyLoadContext.Default"/> so
/// plugin ALCs never bind a second Sdk copy. Skips shipping Sdk.dll beside plugins.
/// </summary>
internal static class PluginAssemblyLoader
{
    public const string SdkAssemblyName = "CampaignVault.PluginSdk";

    public sealed record LoadedPlugin(
        Assembly Assembly,
        string DllPath,
        PluginManifest? Manifest,
        string PackageDirectory,
        IReadOnlyList<string> RulesetDataRoots,
        string? SkillsPathLogged,
        IReadOnlyList<PluginCampaignOption> CampaignOptions);

    /// <summary>A plugin package with no .dll: plugin.json plus RulesetData (parsed YAML, no code runs).</summary>
    public sealed record DataOnlyPlugin(
        PluginManifest Manifest,
        string PackageDirectory,
        IReadOnlyList<string> RulesetDataRoots,
        IReadOnlyList<PluginCampaignOption> CampaignOptions);

    /// <summary>A folder to scan: the server's own Plugins folder (bundled) or a user folder.</summary>
    public sealed record PluginFolder(string Path, bool Bundled);

    public sealed record PluginLoadResult(
        IReadOnlyList<LoadedPlugin> Code,
        IReadOnlyList<DataOnlyPlugin> Data,
        IReadOnlyList<PluginStatus> Catalog);

    public static IReadOnlyList<LoadedPlugin> LoadPluginsFromDirectory(
        string pluginDirectory,
        ILogger? logger = null,
        string? engineVersion = null) =>
        LoadPlugins([new PluginFolder(pluginDirectory, Bundled: true)], null, logger, engineVersion).Code;

    /// <summary>
    /// Scans each folder in order (bundled first, so a user plugin can't shadow a bundled id). Per package: a
    /// disabled id is listed but not loaded; an id already taken by an earlier package, or a minEngineVersion
    /// newer than the host, is listed with an error; otherwise its DLLs load (code) or its RulesetData is used
    /// (data-only). Never throws for a bad package.
    /// </summary>
    public static PluginLoadResult LoadPlugins(
        IReadOnlyList<PluginFolder> folders,
        IReadOnlySet<string>? disabledIds,
        ILogger? logger = null,
        string? engineVersion = null)
    {
        var code = new List<LoadedPlugin>();
        var data = new List<DataOnlyPlugin>();
        var catalog = new List<PluginStatus>();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        engineVersion ??= EngineVersion.Current;
        disabledIds ??= new HashSet<string>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                logger?.LogInformation("Plugin directory '{PluginDir}' does not exist. No plugins will be loaded from it.", folder.Path);
                continue;
            }

            AssemblyLoadContext? alc = null;
            foreach (var package in EnumeratePackages(folder.Path, logger))
            {
                var manifestPath = Path.Combine(package.Directory, "plugin.json");
                var manifest = PluginManifest.TryLoad(manifestPath, out var manifestError);
                var errors = new List<string>();
                if (manifestError != null)
                {
                    logger?.LogWarning("{Error}", manifestError);
                    errors.Add(manifestError);
                }

                if (package.Dlls.Count == 0 && manifest is null)
                    continue; // a data-only package needs a readable plugin.json; nothing to report otherwise

                var id = manifest?.Id ?? Path.GetFileNameWithoutExtension(package.Dlls[0]);
                var enabled = !disabledIds.Contains(id);
                var loaded = false;
                var kind = package.Dlls.Count > 0 ? "code" : "data";

                if (!enabled)
                {
                    logger?.LogInformation("Plugin '{PluginId}' at '{Dir}' is disabled ({Var}).", id, package.Directory, PluginCatalog.DisabledVariable);
                }
                else if (owners.TryGetValue(id, out var owner))
                {
                    var clash = $"Plugin id '{id}' is already provided by '{owner}'; this copy was not loaded.";
                    logger?.LogWarning("{Error} ({Dir})", clash, package.Directory);
                    errors.Add(clash);
                }
                else if (manifest?.MinEngineVersion is { Length: > 0 } min &&
                         PluginManifest.CompareVersions(engineVersion, min) < 0)
                {
                    logger?.LogWarning(
                        "Skipping plugin '{PluginId}' v{PluginVersion} at '{Dir}': requires minEngineVersion {Min}, host is {Host}.",
                        id, manifest.Version, package.Directory, min, engineVersion);
                    errors.Add($"Requires server {min} or newer (this server is {engineVersion}).");
                }
                else if (package.Dlls.Count == 0)
                {
                    var roots = ResolveRulesetDataRoots(package.Directory, manifest);
                    data.Add(new DataOnlyPlugin(manifest!, package.Directory, roots, manifest!.CampaignOptions));
                    loaded = true;
                    logger?.LogInformation(
                        "Loaded data-only plugin: id={PluginId}, version={Version}, dataRoots={Roots}",
                        id, manifest.Version, roots.Count > 0 ? string.Join(';', roots) : "(none)");
                }
                else
                {
                    alc ??= NewLoadContext(folder.Path);
                    foreach (var dllPath in package.Dlls)
                    {
                        if (TryLoadDll(alc, dllPath, package.Directory, manifest, logger, out var plugin, out var error))
                        {
                            code.Add(plugin!);
                            loaded = true;
                        }
                        else
                        {
                            errors.Add(error!);
                        }
                    }
                }

                // Only a loaded package claims its id: a disabled bundled plugin can be replaced by a user copy.
                if (loaded)
                    owners.TryAdd(id, package.Directory);

                catalog.Add(new PluginStatus(
                    id,
                    manifest?.DisplayName is { Length: > 0 } name ? name : id,
                    manifest?.Version ?? "?",
                    manifest?.Author,
                    manifest?.Description,
                    kind,
                    folder.Bundled ? "bundled" : "user",
                    enabled,
                    loaded,
                    package.Directory,
                    manifest?.MinEngineVersion,
                    manifest?.Systems ?? [],
                    manifest?.ModeIds ?? [],
                    manifest?.CampaignOptions ?? [],
                    errors));
            }
        }

        return new PluginLoadResult(code, data, catalog);
    }

    private static AssemblyLoadContext NewLoadContext(string pluginDirectory)
    {
        var alc = new AssemblyLoadContext(pluginDirectory, isCollectible: false);
        alc.Resolving += (_, name) =>
        {
            if (string.Equals(name.Name, SdkAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, SdkAssemblyName, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        };
        return alc;
    }

    private static bool TryLoadDll(
        AssemblyLoadContext alc,
        string dllPath,
        string packageDir,
        PluginManifest? manifest,
        ILogger? logger,
        out LoadedPlugin? plugin,
        out string? error)
    {
        plugin = null;
        error = null;
        try
        {
            // From a stream, so the file isn't held open: a user plugin can be uninstalled while the server runs.
            using var stream = File.OpenRead(dllPath);
            var assembly = alc.LoadFromStream(stream);

            var roots = ResolveRulesetDataRoots(packageDir, manifest);
            var skillsPath = ResolveSkillsPath(packageDir, manifest);
            string? skillsLogged = null;
            if (skillsPath != null && Directory.Exists(skillsPath))
            {
                skillsLogged = skillsPath;
                logger?.LogInformation(
                    "Plugin '{PluginId}' skills folder present at '{SkillsPath}' (sidecar only; host does not load skill files).",
                    manifest?.Id ?? Path.GetFileNameWithoutExtension(dllPath),
                    skillsPath);
            }

            plugin = new LoadedPlugin(assembly, dllPath, manifest, packageDir, roots, skillsLogged, manifest?.CampaignOptions ?? []);
            logger?.LogInformation(
                "Loaded plugin assembly: {FileName} (id={PluginId}, version={Version}, modes={Modes}, dataRoots={Roots}, types pending registry)",
                Path.GetFileName(dllPath),
                manifest?.Id ?? "(no manifest)",
                manifest?.Version ?? "?",
                manifest?.ModeIds is { Count: > 0 } ? string.Join(',', manifest.ModeIds) : "(none advertised)",
                roots.Count > 0 ? string.Join(';', roots) : "(none)");
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to load plugin assembly '{DllPath}'", dllPath);
            error = $"Failed to load {Path.GetFileName(dllPath)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Back-compat shim returning assemblies only.</summary>
    public static IReadOnlyList<Assembly> LoadPluginAssemblies(
        string pluginDirectory,
        ILogger? logger = null,
        string? engineVersion = null) =>
    [
        .. LoadPluginsFromDirectory(pluginDirectory, logger, engineVersion)
            .Select(p => p.Assembly)
    ];

    private sealed record PluginPackage(string Directory, IReadOnlyList<string> Dlls);

    /// <summary>
    /// Flat <c>*.dll</c> in the folder are one package each (manifest: the folder's plugin.json, if any); each
    /// subfolder is one package holding its DLLs, or data-only when it has a plugin.json and no DLL.
    /// </summary>
    private static IEnumerable<PluginPackage> EnumeratePackages(string pluginDirectory, ILogger? logger)
    {
        foreach (var dll in PluginDlls(pluginDirectory, logger))
            yield return new PluginPackage(pluginDirectory, [dll]);

        foreach (var dir in Directory.GetDirectories(pluginDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(dir).StartsWith('.'))
                continue; // e.g. a client's half-finished ".installing-…" extraction
            var dlls = PluginDlls(dir, logger);
            if (dlls.Count > 0 || File.Exists(Path.Combine(dir, "plugin.json")))
                yield return new PluginPackage(dir, dlls);
        }
    }

    private static List<string> PluginDlls(string dir, ILogger? logger)
    {
        var dlls = new List<string>();
        foreach (var dll in Directory.GetFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(dll), SdkAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "Skipping {Dll} in plugin folder — plugins must not ship CampaignVault.PluginSdk.dll (host unifies Sdk types via ALC Default).",
                    dll);
                continue;
            }

            dlls.Add(dll);
        }

        return dlls;
    }

    private static IReadOnlyList<string> ResolveRulesetDataRoots(string packageDir, PluginManifest? manifest)
    {
        var roots = new List<string>();
        var configured = manifest?.RulesetDataRoots is { Count: > 0 }
            ? manifest.RulesetDataRoots
            : ["./RulesetData"];

        foreach (var rel in configured)
        {
            var full = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(packageDir, rel));
            if (Directory.Exists(full))
                roots.Add(full);
        }

        return roots;
    }

    private static string? ResolveSkillsPath(string packageDir, PluginManifest? manifest)
    {
        var rel = string.IsNullOrWhiteSpace(manifest?.SkillsPath) ? "./skills" : manifest!.SkillsPath!;
        return Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(packageDir, rel));
    }


    /// <summary>
    /// Merges plugin-declared <see cref="PluginCampaignOption"/> defaults into <paramref name="systemOptions"/>
    /// without overwriting keys the operator already set.
    /// </summary>
    public static void ApplyCampaignOptionDefaults(
        IEnumerable<LoadedPlugin> plugins,
        IDictionary<string, string> systemOptions,
        ILogger? logger = null)
    {
        foreach (var plugin in plugins)
        {
            ApplyCampaignOptionDefaults(
                plugin.CampaignOptions,
                systemOptions,
                optionSource: plugin.Manifest?.Id ?? plugin.DllPath,
                logger);
        }
    }

    /// <summary>
    /// Merges a flattened set of plugin-declared <see cref="PluginCampaignOption"/> defaults (e.g.
    /// <see cref="PluginDataRoots.DeclaredCampaignOptions"/>) into <paramref name="systemOptions"/> without
    /// overwriting keys already set.
    /// </summary>
    public static void ApplyCampaignOptionDefaults(
        IEnumerable<PluginCampaignOption> options,
        IDictionary<string, string> systemOptions,
        string? optionSource = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(systemOptions);
        foreach (var opt in options)
        {
            if (string.IsNullOrWhiteSpace(opt.Key))
                continue;
            if (systemOptions.ContainsKey(opt.Key))
                continue;
            if (opt.Default is null)
                continue;
            systemOptions[opt.Key] = opt.Default;
            logger?.LogInformation(
                "Applied plugin campaign option default {Key}={Value} from {Source}",
                opt.Key, opt.Default, optionSource ?? "(plugin)");
        }
    }
}
