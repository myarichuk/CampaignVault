using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CampaignVault.Data.Templates;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Tests;

/// <summary>
/// Validates the Extra Content plugin pack (plugins/ExtraContent): the manifest is well-formed, and every spell
/// carries the ec_ prefix (never shadows a core entry) and parses strictly as a <see cref="SpellDefinition"/>.
/// The template loader ignores unknown keys, so a typo'd field would pass silently there; this parse does not.
/// Side-effect free: never invokes the template loader (which writes manifest files into the pack dir).
/// </summary>
public class ExtraContentPackTests
{
    private static readonly string PackDir = FindPackDir();

    private static readonly IDeserializer Strict = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static string FindPackDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "plugins", "ExtraContent");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate plugins/ExtraContent from " + AppContext.BaseDirectory);
    }

    private static string[] SpellFiles() =>
        Directory.EnumerateFiles(Path.Combine(PackDir, "RulesetData", "dnd5e", "spells"), "*.yaml").OrderBy(f => f).ToArray();

    [Fact]
    public void Manifest_HasIdAndDataRoot()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackDir, "plugin.json")));
        var root = doc.RootElement;
        Assert.Equal("com.campaignvault.extra-content", root.GetProperty("id").GetString());
        Assert.Contains("./RulesetData", root.GetProperty("rulesetDataRoots").EnumerateArray().Select(e => e.GetString()));
        Assert.False(Directory.EnumerateFiles(PackDir, "*.dll", SearchOption.AllDirectories).Any(), "a data-only pack ships no DLL");
    }

    [Fact]
    public void Spells_ParseStrictly_AndUseEcPrefix()
    {
        var files = SpellFiles();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var spell = Strict.Deserialize<SpellDefinition>(File.ReadAllText(file));
            var slug = Path.GetFileNameWithoutExtension(file);
            Assert.StartsWith("ec_", slug, StringComparison.Ordinal);
            Assert.Equal(slug, spell.Name);
            Assert.Equal("dnd5e", spell.System);
            Assert.False(string.IsNullOrWhiteSpace(spell.Description), $"{slug} needs DM-facing text");
        }
    }

    [Fact]
    public void RuneSpells_HaveFullSlotLadderAndSave()
    {
        foreach (var file in SpellFiles().Where(f => Path.GetFileName(f).EndsWith("_rune.yaml", StringComparison.Ordinal)))
        {
            var spell = Strict.Deserialize<SpellDefinition>(File.ReadAllText(file));
            var slug = Path.GetFileNameWithoutExtension(file);
            Assert.NotNull(spell.DamageAtSlotLevel);
            Assert.Equal(Enumerable.Range(spell.Level!.Value, 10 - spell.Level.Value), spell.DamageAtSlotLevel!.Keys.OrderBy(k => k));
            Assert.False(string.IsNullOrWhiteSpace(spell.SaveType), $"{slug} needs a save");
            Assert.False(string.IsNullOrWhiteSpace(spell.DamageType), $"{slug} needs a damage type");
        }
    }
}
