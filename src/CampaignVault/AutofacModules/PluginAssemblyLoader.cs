using System.Reflection;
using System.Runtime.Loader;

namespace CampaignVault.AutofacModules;

/// <summary>
/// Task 4.7: Loads plugin assemblies from a directory and makes them available to Autofac.
/// Enables third-party IRulesetModule implementations and other extensibility interfaces.
/// </summary>
internal static class PluginAssemblyLoader
{
    public static IReadOnlyList<Assembly> LoadPluginsFromDirectory(
        string pluginDirectory,
        ILogger? logger = null)
    {
        var loaded = new List<Assembly>();

        if (!Directory.Exists(pluginDirectory))
        {
            logger?.LogInformation("Plugin directory '{PluginDir}' does not exist. No plugins will be loaded.", pluginDirectory);
            return loaded;
        }

        var dllFiles = Directory.GetFiles(pluginDirectory, "*.dll");
        if (dllFiles.Length == 0)
        {
            logger?.LogInformation("No DLL files found in plugin directory '{PluginDir}'.", pluginDirectory);
            return loaded;
        }

        var context = new AssemblyLoadContext(pluginDirectory, isCollectible: false);

        foreach (var dllPath in dllFiles)
        {
            try
            {
                using var stream = File.OpenRead(dllPath);
                var assembly = context.LoadFromStream(stream);
                loaded.Add(assembly);
                logger?.LogInformation("Loaded plugin assembly: {FileName}", Path.GetFileName(dllPath));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load plugin assembly '{DllPath}'", dllPath);
            }
        }

        return loaded;
    }
}
