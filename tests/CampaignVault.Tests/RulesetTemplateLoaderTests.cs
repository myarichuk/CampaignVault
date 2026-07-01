using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampaignVault.Data.Templates;
using Xunit;

namespace CampaignVault.Tests;

public class RulesetTemplateLoaderTests : IDisposable
{
    // Concrete template that matches the YAML fields we'll use in tests
    private record SampleTemplate : RulesetTemplate
    {
        public string? Value { get; init; }
    }

    private readonly string _tempDir;

    public RulesetTemplateLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CampaignVaultLoaderTests_" + Guid.NewGuid());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private RulesetTemplateLoader<SampleTemplate> BuildLoader(
        string? diskDir = null,
        Assembly? assembly = null,
        string prefix = "CampaignVault.Tests.TestData")
    {
        return new RulesetTemplateLoader<SampleTemplate>(
            diskDir ?? _tempDir,
            assembly ?? typeof(RulesetTemplateLoaderTests).Assembly,
            prefix);
    }

    private void WriteYaml(string dir, string fileName, string name, string value)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, fileName),
            $"name: {name}\nvalue: {value}\n");
    }

    // ── Disk loading ──────────────────────────────────────────────────────────

    [Fact]
    public void Load_DiskFile_ReturnsTemplate()
    {
        WriteYaml(_tempDir, "my_pool.yaml", "my_pool", "hello");

        // Use an empty prefix so no embedded resources are matched
        var loader = BuildLoader(prefix: "CampaignVault.Tests.NoSuchPrefix");
        var result = loader.Load();

        Assert.True(result.TryGetValue("my_pool", out var t));
        Assert.Equal("hello", t!.Value);
    }

    [Fact]
    public void Load_TwoFiles_BothPresent()
    {
        WriteYaml(_tempDir, "alpha.yaml", "alpha", "a");
        WriteYaml(_tempDir, "beta.yaml", "beta", "b");

        var loader = BuildLoader(prefix: "CampaignVault.Tests.NoSuchPrefix");
        var result = loader.Load();

        Assert.True(result.ContainsKey("alpha"));
        Assert.True(result.ContainsKey("beta"));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Load_NameIsCaseInsensitive()
    {
        WriteYaml(_tempDir, "spell_slots_1.yaml", "spell_slots_1", "val");

        var loader = BuildLoader(prefix: "CampaignVault.Tests.NoSuchPrefix");
        var result = loader.Load();

        Assert.True(result.TryGetValue("SPELL_SLOTS_1", out _));
        Assert.True(result.TryGetValue("Spell_Slots_1", out _));
    }

    // ── Embedded resource loading ─────────────────────────────────────────────

    [Fact]
    public void Load_EmbeddedResource_ReturnsTemplate()
    {
        // The test assembly has TestData/sample_template.yaml embedded
        // with name: "sample" and value: "embedded_value"
        var loader = BuildLoader(diskDir: _tempDir, prefix: "CampaignVault.Tests.TestData");
        var result = loader.Load();

        Assert.True(result.TryGetValue("sample", out var t));
        Assert.Equal("embedded_value", t!.Value);
    }

    [Fact]
    public void Load_DiskOverridesEmbedded()
    {
        // Write a disk file with the same name ("sample") but different value
        WriteYaml(_tempDir, "sample_template.yaml", "sample", "disk_value");

        var loader = BuildLoader(diskDir: _tempDir, prefix: "CampaignVault.Tests.TestData");
        var result = loader.Load();

        Assert.True(result.TryGetValue("sample", out var t));
        Assert.Equal("disk_value", t!.Value); // disk wins
    }

    [Fact]
    public void Load_ExtractsEmbeddedToDisk_OnFirstLoad()
    {
        // _tempDir does not exist yet — loader should create it and extract the embedded file
        Assert.False(Directory.Exists(_tempDir));

        var loader = BuildLoader(diskDir: _tempDir, prefix: "CampaignVault.Tests.TestData");
        loader.Load();

        var extracted = Path.Combine(_tempDir, "sample_template.yaml");
        Assert.True(File.Exists(extracted), "Embedded file should be extracted to disk on first load.");
    }

    // ── Stale extraction pruning ──────────────────────────────────────────────

    [Fact]
    public void Load_PrunesStaleExtractedFile_WhenRemovedFromEmbedded()
    {
        // Simulate a prior Load() that extracted a now-removed embedded default: write the file
        // to disk exactly as extraction would have, under a prefix with no matching embedded
        // resources this time around (i.e. the embedded source is gone).
        WriteYaml(_tempDir, "removed_class.yaml", "removed_class", "old_value");

        var firstLoader = BuildLoader(prefix: "CampaignVault.Tests.NoSuchPrefix");
        firstLoader.Load(); // establishes the manifest as if this file had just been extracted
        SeedManifestEntry("removed_class.yaml", File.ReadAllText(Path.Combine(_tempDir, "removed_class.yaml")));

        var secondLoader = BuildLoader(prefix: "CampaignVault.Tests.NoSuchPrefix");
        var result = secondLoader.Load();

        Assert.False(File.Exists(Path.Combine(_tempDir, "removed_class.yaml")));
        Assert.False(result.ContainsKey("removed_class"));
    }

    [Fact]
    public void Load_PreservesUserModifiedFile_EvenWhenRemovedFromEmbedded()
    {
        WriteYaml(_tempDir, "edited_class.yaml", "edited_class", "old_value");
        var originalContent = File.ReadAllText(Path.Combine(_tempDir, "edited_class.yaml"));
        SeedManifestEntry("edited_class.yaml", originalContent);

        // DM edits the extracted file locally after it was extracted.
        WriteYaml(_tempDir, "edited_class.yaml", "edited_class", "dm_customized_value");

        var loader = BuildLoader(prefix: "CampaignVault.Tests.NoSuchPrefix");
        var result = loader.Load();

        Assert.True(File.Exists(Path.Combine(_tempDir, "edited_class.yaml")));
        Assert.True(result.TryGetValue("edited_class", out var t));
        Assert.Equal("dm_customized_value", t!.Value);
    }

    private void SeedManifestEntry(string fileName, string extractedContent)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(extractedContent)));
        var manifestPath = Path.Combine(_tempDir, ".extracted-manifest.json");
        var manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))!
            : new Dictionary<string, string>();
        manifest[fileName] = hash;
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
    }
}
