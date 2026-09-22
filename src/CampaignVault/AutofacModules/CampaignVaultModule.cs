using Autofac;
using System.Reflection;
using CampaignVault.Plugins;
using CampaignVault.Schema;
using Microsoft.Extensions.Logging;

namespace CampaignVault.AutofacModules;

/// <summary>
/// Single Autofac module for CampaignVault. All service registration is convention-based
/// via <see cref="ConventionRegistration"/>.
///
/// Task 4.7: Loads plugin assemblies from the Plugins directory and registers their
/// IRulesetModule and other convention-matched implementations.
/// </summary>
public class CampaignVaultModule : Autofac.Module
{
    private readonly string _rulesetDataDirectory;
    private readonly string? _pluginDirectory;
    private readonly ILogger _pluginLogger;

    public CampaignVaultModule() : this(null, null, null) { }

    public CampaignVaultModule(string? rulesetDataDirectory, string? pluginDirectory = null, ILogger? pluginLogger = null)
    {
        _rulesetDataDirectory = rulesetDataDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "RulesetData");
        _pluginDirectory = pluginDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Plugins");
        // Autofac modules are constructed before the host ILoggerFactory is available.
        _pluginLogger = pluginLogger ?? PluginBootstrapLogger.Instance;
    }

    protected override void Load(ContainerBuilder builder)
    {
        var mainAssembly = Assembly.GetExecutingAssembly();
        var assemblies = new List<Assembly> { mainAssembly };

        // Load plugin assemblies from the plugin directory
        if (_pluginDirectory != null && Directory.Exists(_pluginDirectory))
        {
            var plugins = PluginAssemblyLoader.LoadPluginsFromDirectory(_pluginDirectory, _pluginLogger);
            var pluginAssemblies = plugins.Select(p => p.Assembly).ToList();
            assemblies.AddRange(pluginAssemblies);

            PluginDataRoots.Additional = plugins
                .SelectMany(p => p.RulesetDataRoots)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            PluginDataRoots.DeclaredCampaignOptions = plugins
                .SelectMany(p => p.CampaignOptions)
                .Where(o => !string.IsNullOrWhiteSpace(o.Key))
                .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

            // Wire/schema half of plugin WorldChange $types (dispatch already works via FindHandler fallback).
            WorldChangeTypeRegistry.Instance.RegisterPluginAssemblies(pluginAssemblies);
            CommitSchemaModel.Invalidate();

            foreach (var plugin in plugins)
            {
                var registeredTypes = WorldChangeTypeRegistry.Instance.Entries
                    .Where(kv => kv.Value.Assembly == plugin.Assembly)
                    .Select(kv => kv.Key)
                    .OrderBy(x => x)
                    .ToList();
                if (registeredTypes.Count > 0)
                {
                    _pluginLogger.LogInformation(
                        "Plugin {PluginId} registered $types: {Types}",
                        plugin.Manifest?.Id ?? plugin.DllPath,
                        string.Join(", ", registeredTypes));
                }
            }
        }

        ConventionRegistration.Register(builder, assemblies, _rulesetDataDirectory);
    }

    private static class PluginBootstrapLogger
    {
        internal static readonly ILogger Instance = LoggerFactory
            .Create(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Information);
            })
            .CreateLogger("CampaignVault.Plugins");
    }
}