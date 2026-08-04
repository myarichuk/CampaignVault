using System;
using System.IO;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Task 4.6: Directory-driven providers enable data-only plugins without hardcoded registration.
/// These tests verify discovery from both embedded resources and disk directories.
/// </summary>
public class Phase4_DirectoryDrivenProvidersTests
{
    [Fact]
    public void SpellProvider_DiscoveryFromEmbedded_WhenDiskEmpty_LoadsDnd5eAndPf2e()
    {
        // Arrange: empty temp directory, assembly with embedded resources
        using var tempDir = new TemporaryDirectory();
        var assembly = typeof(SpellDefinitionProvider).Assembly;

        // Act
        var provider = new SpellDefinitionProvider(tempDir.Path, assembly);

        // Assert: dnd5e and pf2e spells loaded from embedded resources
        var dnd5eSpells = provider.GetSpellsForSystem("dnd5e");
        var pf2eSpells = provider.GetSpellsForSystem("pf2e");

        Assert.NotEmpty(dnd5eSpells);
        Assert.NotEmpty(pf2eSpells);
    }

    [Fact]
    public void RaceProvider_DiscoveryFromDisk_WithFabricatedSystem_LoadsFromDiskDirectory()
    {
        // Arrange: create a fabricated system with races data on disk
        using var tempDir = new TemporaryDirectory();
        var assembly = typeof(RaceDefinitionProvider).Assembly;

        // Create fabricated/races/ directory
        var fabricatedDir = System.IO.Path.Combine(tempDir.Path, "fabricated", "races");
        Directory.CreateDirectory(fabricatedDir);

        // Write a minimal race definition YAML
        var raceYaml = """
            name: CustomRace
            description: A custom race for testing
            size: Medium
            speed: 30
            ability_score_increases:
              strength: 2
            """;
        System.IO.File.WriteAllText(System.IO.Path.Combine(fabricatedDir, "custom_race.yaml"), raceYaml);

        // Act
        var provider = new RaceDefinitionProvider(tempDir.Path, assembly);

        // Assert: fabricated system discovered and race loaded
        var races = provider.GetRacesForSystem("fabricated");
        Assert.NotEmpty(races);
        Assert.True(races.ContainsKey("CustomRace"));
    }

    [Fact]
    public void RaceProvider_SubfolderCandidates_PrefersRacesOverAncestries_WhenBothExist()
    {
        // Arrange: create a system with both races and ancestries directories
        using var tempDir = new TemporaryDirectory();
        var assembly = typeof(RaceDefinitionProvider).Assembly;

        var systemDir = System.IO.Path.Combine(tempDir.Path, "test_system");
        Directory.CreateDirectory(System.IO.Path.Combine(systemDir, "races"));
        Directory.CreateDirectory(System.IO.Path.Combine(systemDir, "ancestries"));

        // Write a race definition
        var raceYaml = """
            name: RaceFromRacesDir
            description: From races folder
            size: Medium
            speed: 30
            ability_score_increases:
              strength: 1
            """;
        System.IO.File.WriteAllText(System.IO.Path.Combine(systemDir, "races", "race_file.yaml"), raceYaml);

        // Write an ancestry definition in ancestries folder (should not be used)
        var ancestryYaml = """
            name: AncestryFromAncestries
            description: From ancestries folder
            size: Medium
            speed: 30
            ability_score_increases:
              strength: 1
            """;
        System.IO.File.WriteAllText(System.IO.Path.Combine(systemDir, "ancestries", "ancestry_file.yaml"), ancestryYaml);

        // Act
        var provider = new RaceDefinitionProvider(tempDir.Path, assembly);
        var races = provider.GetRacesForSystem("test_system");

        // Assert: races folder was preferred (contains the race)
        Assert.NotEmpty(races);
        Assert.True(races.ContainsKey("RaceFromRacesDir"));
        Assert.False(races.ContainsKey("AncestryFromAncestries"));
    }

    [Fact]
    public void ClassProvider_CaseInsensitiveDiscovery_SWADE_ResolvesAs_swade()
    {
        // Arrange: create a system directory with unusual casing
        using var tempDir = new TemporaryDirectory();
        var assembly = typeof(ClassDefinitionProvider).Assembly;

        var systemDir = System.IO.Path.Combine(tempDir.Path, "SWADE");
        var classesDir = System.IO.Path.Combine(systemDir, "classes");
        Directory.CreateDirectory(classesDir);

        var classYaml = """
            name: Soldier
            description: Military training
            hit_die: d10
            """;
        System.IO.File.WriteAllText(System.IO.Path.Combine(classesDir, "soldier.yaml"), classYaml);

        // Act
        var provider = new ClassDefinitionProvider(tempDir.Path, assembly);

        // Assert: system resolvable by original name, discovered name, and lowercase
        var classes_swade = provider.GetClassesForSystem("SWADE");
        var classes_swade_lowercase = provider.GetClassesForSystem("swade");

        Assert.NotEmpty(classes_swade);
        Assert.NotEmpty(classes_swade_lowercase);
        Assert.True(classes_swade.ContainsKey("Soldier"));
        Assert.True(classes_swade_lowercase.ContainsKey("Soldier"));
    }

    [Fact]
    public void CreatureProvider_EmbeddedAndDiskUnion_DiscoversBoth()
    {
        // Arrange: temp dir has one system, embedded has others
        using var tempDir = new TemporaryDirectory();
        var assembly = typeof(CreatureDefinitionProvider).Assembly;

        // Add a disk-only system
        var customDir = System.IO.Path.Combine(tempDir.Path, "custom", "creatures");
        Directory.CreateDirectory(customDir);
        var creatureYaml = """
            name: CustomBeast
            description: Custom creature
            armor_class: 15
            hit_points: 50
            """;
        System.IO.File.WriteAllText(System.IO.Path.Combine(customDir, "custom_beast.yaml"), creatureYaml);

        // Act
        var provider = new CreatureDefinitionProvider(tempDir.Path, assembly);

        // Assert: both embedded (dnd5e, pf2e) and disk (custom) systems resolve
        var dnd5eCreatures = provider.GetCreaturesForSystem("dnd5e");
        var customCreatures = provider.GetCreaturesForSystem("custom");

        Assert.NotEmpty(dnd5eCreatures);
        Assert.NotEmpty(customCreatures);
        Assert.True(customCreatures.ContainsKey("CustomBeast"));
    }

    private class TemporaryDirectory : IDisposable
    {
        public string Path { get; }

        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
