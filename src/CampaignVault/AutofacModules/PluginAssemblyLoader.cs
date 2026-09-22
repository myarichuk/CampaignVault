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

    public static IReadOnlyList<LoadedPlugin> LoadPluginsFromDirectory(
        string pluginDirectory,
        ILogger? logger = null,
        string? engineVersion = null)
    {
        var loaded = new List<LoadedPlugin>();
        engineVersion ??= EngineVersion.Current;

        if (!Directory.Exists(pluginDirectory))
        {
            logger?.LogInformation("Plugin directory '{PluginDir}' does not exist. No plugins will be loaded.", pluginDirectory);
            return loaded;
        }

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

        foreach (var dllPath in EnumeratePluginDlls(pluginDirectory, logger))
        {
            var packageDir = Path.GetDirectoryName(dllPath)!;
            var manifestPath = Path.Combine(packageDir, "plugin.json");
            var manifest = PluginManifest.TryLoad(manifestPath, out var manifestError);
            if (manifestError != null)
            {
                logger?.LogWarning("{Error}", manifestError);
            }

            if (manifest?.MinEngineVersion is { Length: > 0 } min &&
                PluginManifest.CompareVersions(engineVersion, min) < 0)
            {
                logger?.LogWarning(
                    "Skipping plugin '{PluginId}' v{PluginVersion} at '{Dll}': requires minEngineVersion {Min}, host is {Host}.",
                    manifest.Id, manifest.Version, dllPath, min, engineVersion);
                continue;
            }

            try
            {
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

                loaded.Add(new LoadedPlugin(assembly, dllPath, manifest, packageDir, roots, skillsLogged, manifest?.CampaignOptions ?? []));
                logger?.LogInformation(
                    "Loaded plugin assembly: {FileName} (id={PluginId}, version={Version}, modes={Modes}, dataRoots={Roots}, types pending registry)",
                    Path.GetFileName(dllPath),
                    manifest?.Id ?? "(no manifest)",
                    manifest?.Version ?? "?",
                    manifest?.ModeIds is { Count: > 0 } ? string.Join(',', manifest.ModeIds) : "(none advertised)",
                    roots.Count > 0 ? string.Join(';', roots) : "(none)");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load plugin assembly '{DllPath}'", dllPath);
            }
        }

        return loaded;
    }

    /// <summary>Back-compat shim returning assemblies only.</summary>
    public static IReadOnlyList<Assembly> LoadPluginAssemblies(
        string pluginDirectory,
        ILogger? logger = null,
        string? engineVersion = null) =>
        LoadPluginsFromDirectory(pluginDirectory, logger, engineVersion)
            .Select(p => p.Assembly)
            .ToList();

    private static IEnumerable<string> EnumeratePluginDlls(string pluginDirectory, ILogger? logger)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var flat = Directory.GetFiles(pluginDirectory, "*.dll");
        var nested = Directory.Exists(pluginDirectory)
            ? Directory.GetDirectories(pluginDirectory)
                .SelectMany(dir => Directory.GetFiles(dir, "*.dll"))
            : [];

        foreach (var dll in flat.Concat(nested))
        {
            var fileName = Path.GetFileName(dll);
            if (string.Equals(fileName, SdkAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "Skipping {Dll} in plugin folder — plugins must not ship CampaignVault.PluginSdk.dll (host unifies Sdk types via ALC Default).",
                    dll);
                continue;
            }

            if (seen.Add(Path.GetFullPath(dll)))
                yield return dll;
        }
    }

    private static IReadOnlyList<string> ResolveRulesetDataRoots(string packageDir, PluginManifest? manifest)
    {
        var roots = new List<string>();
        var configured = manifest?.RulesetDataRoots is { Count: > 0 }
            ? manifest.RulesetDataRoots
            : new List<string> { "./RulesetData" };

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
