using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CampaignVault.AutofacModules;
using CampaignVault.Hosting;
using CampaignVault.Plugins;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>N6: user plugin folders, the disabled list, data-only packages and the GET /plugins catalog.</summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-plugin-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundled;
    private readonly string _user;

    public PluginCatalogTests()
    {
        _bundled = Path.Combine(_root, "bundled");
        _user = Path.Combine(_root, "user");
        Directory.CreateDirectory(_bundled);
        Directory.CreateDirectory(_user);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string DataPackage(string folder, string name, string id, string extraJson = "")
    {
        var dir = Path.Combine(folder, name);
        var items = Path.Combine(dir, "RulesetData", "dnd5e", "items");
        Directory.CreateDirectory(items);
        File.WriteAllText(Path.Combine(items, "lantern_of_tests.yaml"), "name: Lantern of Tests\n");
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{"id":"{{id}}","displayName":"{{name}}","version":"1.2.0","author":"Scratch Author","description":"Adds a lantern."{{extraJson}}}""");
        return dir;
    }

    private IReadOnlyList<PluginAssemblyLoader.PluginFolder> Folders() =>
    [
        new(_bundled, Bundled: true),
        new(_user, Bundled: false),
    ];

    [Fact]
    public void Data_only_package_in_a_user_folder_loads_its_ruleset_data()
    {
        var dir = DataPackage(_user, "Lanterns", "com.example.lanterns",
            ""","campaignOptions":[{"key":"lanternFuel","type":"bool","default":"true"}]""");

        var result = PluginAssemblyLoader.LoadPlugins(Folders(), null, engineVersion: "0.11.0");

        Assert.Empty(result.Code);
        var data = Assert.Single(result.Data);
        Assert.Equal("com.example.lanterns", data.Manifest.Id);
        Assert.Equal(Path.Combine(dir, "RulesetData"), Assert.Single(data.RulesetDataRoots));
        Assert.Equal("lanternFuel", Assert.Single(data.CampaignOptions).Key);

        var entry = Assert.Single(result.Catalog);
        Assert.Equal("data", entry.Kind);
        Assert.Equal("user", entry.Source);
        Assert.True(entry.Enabled);
        Assert.True(entry.Loaded);
        Assert.Equal("Lanterns", entry.Name);
        Assert.Equal("Scratch Author", entry.Author);
        Assert.Equal("Adds a lantern.", entry.Description);
        Assert.Empty(entry.Errors);
    }

    [Fact]
    public void Disabled_ids_are_listed_but_not_loaded()
    {
        DataPackage(_user, "Lanterns", "com.example.lanterns");
        PluginTestDrop.InstallCrafting(_bundled);

        var result = PluginAssemblyLoader.LoadPlugins(
            Folders(), PluginCatalog.ParseDisabled("com.example.lanterns, COM.CAMPAIGNVAULT.CRAFTING"), engineVersion: "0.11.0");

        Assert.Empty(result.Code);
        Assert.Empty(result.Data);
        Assert.Equal(2, result.Catalog.Count);
        Assert.All(result.Catalog, e => Assert.False(e.Enabled));
        Assert.All(result.Catalog, e => Assert.False(e.Loaded));
        Assert.Equal("code", result.Catalog.Single(e => e.Id == "com.campaignvault.crafting").Kind);
    }

    [Fact]
    public void A_user_package_cannot_take_a_bundled_id_unless_the_bundled_one_is_disabled()
    {
        DataPackage(_bundled, "Lanterns", "com.example.lanterns");
        DataPackage(_user, "LanternsFork", "com.example.lanterns");

        var clash = PluginAssemblyLoader.LoadPlugins(Folders(), null, engineVersion: "0.11.0");
        Assert.Single(clash.Data);
        Assert.Equal(_bundled, Path.GetDirectoryName(clash.Data[0].PackageDirectory));
        var rejected = clash.Catalog.Single(e => e.Source == "user");
        Assert.False(rejected.Loaded);
        Assert.Contains(rejected.Errors, e => e.Contains("already provided", StringComparison.Ordinal));

        var replaced = PluginAssemblyLoader.LoadPlugins(
            Folders(), PluginCatalog.ParseDisabled("com.example.lanterns"), engineVersion: "0.11.0");
        // The disabled list names an id, so it disables every copy of it.
        Assert.Empty(replaced.Data);
    }

    [Fact]
    public void Too_new_and_broken_packages_are_reported_with_errors()
    {
        DataPackage(_user, "Future", "com.example.future", ""","minEngineVersion":"99.0.0" """);
        var broken = Path.Combine(_user, "Broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "plugin.json"), "{ not json");
        File.WriteAllBytes(Path.Combine(broken, "Broken.dll"), [1, 2, 3]);
        // A client's interrupted extraction is ignored.
        DataPackage(_user, ".installing-123", "com.example.partial");

        var result = PluginAssemblyLoader.LoadPlugins(Folders(), null, engineVersion: "0.11.0");

        Assert.Empty(result.Code);
        Assert.Empty(result.Data);
        var future = result.Catalog.Single(e => e.Id == "com.example.future");
        Assert.Contains(future.Errors, e => e.Contains("99.0.0", StringComparison.Ordinal));
        var bad = result.Catalog.Single(e => e.Id == "Broken");
        Assert.Equal("code", bad.Kind);
        Assert.False(bad.Loaded);
        Assert.Equal(2, bad.Errors.Count); // unreadable manifest + unloadable DLL
        Assert.DoesNotContain(result.Catalog, e => e.Id == "com.example.partial");
    }

    [Fact]
    public void Missing_folders_are_skipped()
    {
        var result = PluginAssemblyLoader.LoadPlugins(
            [new(Path.Combine(_root, "nope"), true), new(_user, false)], null, engineVersion: "0.11.0");
        Assert.Empty(result.Catalog);
    }

    [Fact]
    public void Env_lists_parse()
    {
        var sep = Path.PathSeparator;
        Assert.Equal(["/a", "/b c"], PluginCatalog.ParseDirectories($" /a {sep}{sep}/b c "));
        Assert.Empty(PluginCatalog.ParseDirectories(null));
        var disabled = PluginCatalog.ParseDisabled("a.b; c.d\ne.f,,");
        Assert.Equal(3, disabled.Count);
        Assert.Contains("A.B", disabled);
        Assert.Empty(PluginCatalog.ParseDisabled("  "));
    }

    [Fact]
    public void Catalog_serializes_as_the_client_reads_it()
    {
        DataPackage(_user, "Lanterns", "com.example.lanterns");
        var result = PluginAssemblyLoader.LoadPlugins(Folders(), null, engineVersion: "0.11.0");

        // Minimal APIs use the web defaults (camelCase).
        var json = JsonSerializer.Serialize(new { plugins = result.Catalog }, JsonSerializerOptions.Web);
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement.GetProperty("plugins")[0];
        foreach (var field in new[] { "id", "name", "version", "author", "description", "kind", "source", "enabled", "loaded", "directory", "systems", "modeIds", "campaignOptions", "errors" })
        {
            Assert.True(p.TryGetProperty(field, out _), field);
        }

        Assert.Equal("1 loaded (1 user)", HostSwitches.DescribePlugins(result.Catalog));
        Assert.Equal("0 loaded, 1 disabled", HostSwitches.DescribePlugins(
            PluginAssemblyLoader.LoadPlugins(Folders(), PluginCatalog.ParseDisabled("com.example.lanterns"), engineVersion: "0.11.0").Catalog));
    }
}
