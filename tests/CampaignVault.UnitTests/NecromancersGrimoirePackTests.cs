using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Validates the Necromancer's Grimoire plugin pack
/// (plugins/NecromancersGrimoire): every spell carries the ng_ prefix
/// (never shadows a core SRD entry), declares the system matching its
/// directory, has a full slot-level damage ladder, and the two known
/// above-curve outliers stay trimmed (crypt flare at 6d10, the second
/// bound shade gated to a 6th-level slot).
/// Side-effect free: reads YAML as text, never invokes the template loader
/// (which would extract defaults and write manifest files into the pack dir).
/// </summary>
public class NecromancersGrimoirePackTests
{
    private static readonly string PackRoot = FindPackRoot();

    private static string FindPackRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "plugins", "NecromancersGrimoire", "RulesetData");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate plugins/NecromancersGrimoire/RulesetData from " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, List<Dictionary<string, string>>> LoadSpells()
    {
        var perSystem = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var systemDir in Directory.EnumerateDirectories(PackRoot))
        {
            var spellsDir = Path.Combine(systemDir, "spells");
            if (!Directory.Exists(spellsDir))
                continue;

            var system = Path.GetFileName(systemDir);
            var entries = new List<Dictionary<string, string>>();
            foreach (var file in Directory.EnumerateFiles(spellsDir, "*.yaml").OrderBy(f => f))
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
                    if (key is "name" or "system" or "level" or "damageType" or "saveType")
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
    public void Pack_EverySpell_UsesNgPrefix_AndDeclaresMatchingSystem()
    {
        var pack = LoadSpells();

        Assert.Contains("dnd5e", pack.Keys);
        foreach (var (system, entries) in pack)
        {
            Assert.NotEmpty(entries);
            foreach (var entry in entries)
            {
                Assert.True(entry.TryGetValue("name", out var name), $"{entry["__file"]}: missing name:");
                Assert.StartsWith("ng_", name);

                Assert.True(entry.TryGetValue("system", out var declared), $"{entry["__file"]}: missing system:");
                Assert.Equal(system, declared);
            }

            var names = entries.Select(e => e["name"]).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Pack_DamageSpells_HaveFullSlotLadder_AndSave()
    {
        var pack = LoadSpells();

        foreach (var entry in pack["dnd5e"])
        {
            if (!entry.ContainsKey("damageType"))
                continue; // ng_bind_shade: summon, not damage.

            Assert.True(entry.TryGetValue("level", out var levelText), $"{entry["__file"]}: missing level:");
            var level = int.Parse(levelText);

            if (level == 0)
            {
                // Cantrips scale by character level, not slot.
                foreach (var charLevel in new[] { 1, 5, 11, 17 })
                    Assert.Contains($"  {charLevel}: ", entry["__text"]);
                continue;
            }

            if (!entry["__text"].Contains("requiresAttackRoll: true"))
                Assert.True(entry.TryGetValue("saveType", out _), $"{entry["__file"]}: missing saveType:");

            // Every slot from the spell's level through 9 must have a damage line.
            for (var slot = level; slot <= 9; slot++)
                Assert.Contains($"  {slot}: ", entry["__text"]);
        }
    }

    [Fact]
    public void Pack_NewSpells_HaveExpectedBaseDice()
    {
        var pack = LoadSpells();
        var byName = pack["dnd5e"].ToDictionary(e => e["name"], StringComparer.OrdinalIgnoreCase);

        Assert.True(byName.TryGetValue("ng_red_harvest", out var harvest), "ng_red_harvest missing");
        Assert.Contains("  3: 4d8", harvest["__text"]);

        Assert.True(byName.TryGetValue("ng_pallid_chain", out var chain), "ng_pallid_chain missing");
        Assert.Contains("  4: 4d8", chain["__text"]);

        Assert.True(byName.TryGetValue("ng_ebon_tithe", out var tithe), "ng_ebon_tithe missing");
        Assert.Contains("  2: 2d6", tithe["__text"]);
    }

    [Fact]
    public void Pack_CryptFlare_StaysTrimmed()
    {
        var pack = LoadSpells();
        var flare = pack["dnd5e"].Single(e => e["name"] == "ng_crypt_flare");

        Assert.Contains("  5: 6d10", flare["__text"]);
        Assert.DoesNotContain("  5: 8d10", flare["__text"]);
    }

    [Fact]
    public void Pack_BindShade_SecondShadeStaysGated()
    {
        var pack = LoadSpells();
        var shade = pack["dnd5e"].Single(e => e["name"] == "ng_bind_shade");

        Assert.Contains("    4: 1", shade["__text"]);
        Assert.Contains("    6: 2", shade["__text"]);
    }
}
