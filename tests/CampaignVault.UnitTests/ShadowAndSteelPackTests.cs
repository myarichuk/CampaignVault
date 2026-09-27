using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Validates the Shadow &amp; Steel plugin seed pack (plugins/ShadowAndSteel):
/// every blueprint carries the ss_ prefix (never shadows regenerated core or
/// medieval_ entries), declares the system matching its directory, uses a known
/// category, and weapon/armor entries carry their core mechanical key.
/// Side-effect free: reads YAML as text, never invokes the template loader
/// (which would extract defaults and write manifest files into the pack dir).
/// </summary>
public class ShadowAndSteelPackTests
{
    private static readonly string PackRoot = FindPackRoot();

    private static string FindPackRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "plugins", "ShadowAndSteel", "RulesetData");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate plugins/ShadowAndSteel/RulesetData from " + AppContext.BaseDirectory);
    }

    private static readonly HashSet<string> KnownCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weapon", "Armor", "Consumable", "Tool",
    };

    private static Dictionary<string, List<Dictionary<string, string>>> LoadPack()
    {
        var perSystem = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var systemDir in Directory.EnumerateDirectories(PackRoot))
        {
            var itemsDir = Path.Combine(systemDir, "items");
            if (!Directory.Exists(itemsDir))
                continue;

            var system = Path.GetFileName(systemDir);
            var entries = new List<Dictionary<string, string>>();
            foreach (var file in Directory.EnumerateFiles(itemsDir, "*.yaml").OrderBy(f => f))
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(file))
                {
                    // Top-level keys only: no leading whitespace, "key: value" shape.
                    if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#'))
                        continue;
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var key = line[..colon].Trim();
                    if (key is "name" or "system" or "category")
                        fields[key] = line[(colon + 1)..].Trim();
                }

                fields["__file"] = Path.GetFileName(file);
                fields["__text"] = File.ReadAllText(file);
                entries.Add(fields);
            }

            perSystem[system] = entries;
        }

        return perSystem;
    }

    [Fact]
    public void Pack_CoversThreeSystems()
    {
        var pack = LoadPack();

        Assert.Contains("dnd5e", pack.Keys);
        Assert.Contains("pf2e", pack.Keys);
        Assert.Contains("swade", pack.Keys);
    }

    [Fact]
    public void Pack_EveryEntry_UsesSsPrefix_DeclaresMatchingSystem_AndKnownCategory()
    {
        var pack = LoadPack();

        foreach (var (system, entries) in pack)
        {
            Assert.NotEmpty(entries);
            foreach (var entry in entries)
            {
                Assert.True(entry.TryGetValue("name", out var name), $"{entry["__file"]}: missing name:");
                Assert.StartsWith("ss_", name);

                Assert.True(entry.TryGetValue("system", out var declared), $"{entry["__file"]}: missing system:");
                Assert.Equal(system, declared);

                Assert.True(entry.TryGetValue("category", out var category), $"{entry["__file"]}: missing category:");
                Assert.Contains(category, KnownCategories);
            }

            var names = entries.Select(e => e["name"]).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Pack_MeetsPerSystemMinimums()
    {
        var pack = LoadPack();

        foreach (var (system, entries) in pack)
        {
            var byCategory = entries.GroupBy(e => e["category"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var weapons = byCategory.GetValueOrDefault("Weapon");
            var armor = byCategory.GetValueOrDefault("Armor");
            var gear = byCategory.GetValueOrDefault("Consumable") + byCategory.GetValueOrDefault("Tool");

            Assert.True(weapons >= 5, $"{system}: only {weapons} weapons, expected >= 5");
            Assert.True(armor >= 4, $"{system}: only {armor} armor, expected >= 4");
            Assert.True(gear >= 2, $"{system}: only {gear} gear entries, expected >= 2");
        }
    }

    [Fact]
    public void Pack_WeaponsCarryDamage_ArmorCarriesAcBonus()
    {
        var pack = LoadPack();

        foreach (var (system, entries) in pack)
        {
            foreach (var entry in entries)
            {
                if (entry["category"].Equals("Weapon", StringComparison.OrdinalIgnoreCase))
                    Assert.Contains("damage:", entry["__text"]);
                if (entry["category"].Equals("Armor", StringComparison.OrdinalIgnoreCase))
                    Assert.Contains("acBonus:", entry["__text"]);
            }
        }
    }
}
