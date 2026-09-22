using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Autofac;
using CampaignVault.AutofacModules;
using CampaignVault.Data.Templates;
using CampaignVault.Plugins;
using CampaignVault.Rulesets.Modes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CampaignVault.Tests;

public class PluginAssemblyLoaderTests
{
    [Fact]
    public void PluginManifest_parses_and_compare_versions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv-plugin-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "plugin.json");
            File.WriteAllText(path, """
                {
                  "id": "com.example.test",
                  "displayName": "Test",
                  "version": "1.2.3",
                  "minEngineVersion": "0.1.0",
                  "modeIds": ["crafting"],
                  "rulesetDataRoots": ["./RulesetData"],
                  "skillsPath": "./skills"
                }
                """);
            var manifest = PluginManifest.TryLoad(path, out var error);
            Assert.Null(error);
            Assert.NotNull(manifest);
            Assert.Equal("com.example.test", manifest!.Id);
            Assert.Equal("1.2.3", manifest.Version);
            Assert.Contains("crafting", manifest.ModeIds);
            Assert.True(PluginManifest.CompareVersions("0.2.0", "0.1.0") > 0);
            Assert.True(PluginManifest.CompareVersions("0.1.0", "0.2.0") < 0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Missing_manifest_is_back_compat()
    {
        Assert.Null(PluginManifest.TryLoad(Path.Combine(Path.GetTempPath(), "no-such-plugin.json"), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ApplyCampaignOptionDefaults_FillsMissingKeys_WithoutOverwritingExisting()
    {
        var options = new List<PluginCampaignOption>
        {
            new() { Key = "lingeringInjuries", Default = "true" },
            new() { Key = "encumbrance", Default = "strict" },
        };
        var systemOptions = new Dictionary<string, string> { ["encumbrance"] = "off" };

        PluginAssemblyLoader.ApplyCampaignOptionDefaults(options, systemOptions);

        Assert.Equal("true", systemOptions["lingeringInjuries"]);
        Assert.Equal("off", systemOptions["encumbrance"]); // operator/DM value untouched
    }

    [Fact]
    public void ApplyCampaignOptionDefaults_SkipsKeysWithNoDeclaredDefault()
    {
        var options = new List<PluginCampaignOption> { new() { Key = "customFirearms", Default = null } };
        var systemOptions = new Dictionary<string, string>();

        PluginAssemblyLoader.ApplyCampaignOptionDefaults(options, systemOptions);

        Assert.False(systemOptions.ContainsKey("customFirearms"));
    }

    [Fact]
    public void Discover_merges_additional_roots_last_wins()
    {
        var primary = Path.Combine(Path.GetTempPath(), "cv-rd-primary-" + Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(Path.GetTempPath(), "cv-rd-plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(primary, "dnd5e", "conditions"));
            Directory.CreateDirectory(Path.Combine(plugin, "dnd5e", "conditions"));
            File.WriteAllText(Path.Combine(primary, "dnd5e", "conditions", "a.yaml"), "name: a\n");
            File.WriteAllText(Path.Combine(plugin, "dnd5e", "conditions", "b.yaml"), "name: b\n");

            var discovered = RulesetDataSystemDiscovery.Discover(
                primary,
                typeof(PluginAssemblyLoaderTests).Assembly,
                ["conditions"],
                [plugin]).ToList();

            var dnd = Assert.Single(discovered, x => x.systemSlug.Equals("dnd5e", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(plugin, dnd.diskRoot);
        }
        finally
        {
            if (Directory.Exists(primary)) Directory.Delete(primary, true);
            if (Directory.Exists(plugin)) Directory.Delete(plugin, true);
        }
    }

    [Fact]
    public void Loader_skips_minEngineVersion_mismatch_without_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "cv-plugins-" + Guid.NewGuid().ToString("N"));
        var pkg = Path.Combine(root, "OldPlugin");
        Directory.CreateDirectory(pkg);
        try
        {
            File.WriteAllText(Path.Combine(pkg, "plugin.json"), """
                {"id":"com.example.old","version":"0.0.1","minEngineVersion":"99.0.0"}
                """);
            File.Copy(typeof(IInteractionMode).Assembly.Location, Path.Combine(pkg, "OldPlugin.dll"), overwrite: true);
            var logger = new CollectingLogger();
            var loaded = PluginAssemblyLoader.LoadPluginsFromDirectory(root, logger, engineVersion: "0.2.0");
            Assert.Empty(loaded);
            Assert.Contains(logger.Messages, m => m.Contains("minEngineVersion", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Skipping plugin", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Loader_skips_Sdk_dll_in_plugin_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "cv-plugins-sdk-" + Guid.NewGuid().ToString("N"));
        var pkg = Path.Combine(root, "BadDrop");
        Directory.CreateDirectory(pkg);
        try
        {
            var sdkPath = Path.Combine(pkg, "CampaignVault.PluginSdk.dll");
            File.Copy(typeof(IInteractionMode).Assembly.Location, sdkPath, overwrite: true);
            var loaded = PluginAssemblyLoader.LoadPluginsFromDirectory(root);
            Assert.Empty(loaded);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Shared_type_identity_IInteractionMode_is_from_Default_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "cv-plugins-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dest = PluginTestDrop.InstallCrafting(root);
            Assert.False(File.Exists(Path.Combine(dest, "CampaignVault.PluginSdk.dll")));

            var plugins = PluginAssemblyLoader.LoadPluginsFromDirectory(root);
            var plugin = Assert.Single(plugins);
            var pluginAlc = AssemblyLoadContext.GetLoadContext(plugin.Assembly);
            Assert.NotNull(pluginAlc);
            Assert.NotSame(AssemblyLoadContext.Default, pluginAlc);

            var modeType = plugin.Assembly.GetTypes().First(t => typeof(IInteractionMode).IsAssignableFrom(t) && !t.IsAbstract);
            var mode = (IInteractionMode)Activator.CreateInstance(modeType)!;
            Assert.True(mode is IInteractionMode);
            Assert.Same(typeof(IInteractionMode).Assembly, mode.GetType().GetInterface(nameof(IInteractionMode))!.Assembly);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IInteractionMode).Assembly));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Module_logs_minEngineVersion_skip_through_supplied_logger()
    {
        var root = Path.Combine(Path.GetTempPath(), "cv-plugins-modlog-" + Guid.NewGuid().ToString("N"));
        var pkg = Path.Combine(root, "OldPlugin");
        Directory.CreateDirectory(pkg);
        try
        {
            File.WriteAllText(Path.Combine(pkg, "plugin.json"), """
                {"id":"com.example.old","version":"0.0.1","minEngineVersion":"99.0.0"}
                """);
            File.Copy(typeof(IInteractionMode).Assembly.Location, Path.Combine(pkg, "OldPlugin.dll"), overwrite: true);
            var logger = new CollectingLogger();
            var builder = new ContainerBuilder();
            builder.RegisterModule(new CampaignVaultModule(
                Path.Combine(AppContext.BaseDirectory, "RulesetData"),
                root,
                logger));
            builder.RegisterInstance(new TestFakeEmbeddingService()).As<CampaignVault.Services.ILocalEmbeddingService>();
            using var container = builder.Build();
            Assert.Contains(logger.Messages, m => m.Contains("Skipping plugin", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

internal sealed class CollectingLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
