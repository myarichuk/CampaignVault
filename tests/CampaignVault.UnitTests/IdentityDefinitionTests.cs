using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class IdentityDefinitionTests
{
    private static readonly (
        RaceDefinitionProvider Races,
        BackgroundDefinitionProvider Backgrounds,
        FeatDefinitionProvider Feats,
        ResourcePoolProvider Pools,
        ClassDefinitionProvider Classes,
        ResourcePoolInitializer Initializer)
        Services = CreateServices();

    private static (
        RaceDefinitionProvider,
        BackgroundDefinitionProvider,
        FeatDefinitionProvider,
        ResourcePoolProvider,
        ClassDefinitionProvider,
        ResourcePoolInitializer)
        CreateServices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_identity_test_" + Guid.NewGuid());
        var assembly = typeof(ResourcePoolProvider).Assembly;
        var pools = new ResourcePoolProvider(dir, assembly);
        var classes = new ClassDefinitionProvider(dir, assembly);
        var races = new RaceDefinitionProvider(dir, assembly);
        var backgrounds = new BackgroundDefinitionProvider(dir, assembly);
        var feats = new FeatDefinitionProvider(dir, assembly);
        var initializer = new ResourcePoolInitializer(pools, classes, feats);
        return (races, backgrounds, feats, pools, classes, initializer);
    }

    [Fact]
    public void RaceDefinition_Elf_HasCorrectTraits()
    {
        var races = Services.Races.GetRacesForSystem(RulesetSystem.Dnd5e);

        Assert.True(races.TryGetValue("elf", out var elf));
        Assert.NotNull(elf);
        Assert.Contains("Darkvision", elf.Traits);
        Assert.Contains("Fey Ancestry", elf.Traits);
        Assert.Equal(2, elf.AbilityBonuses["Dexterity"]);
    }

    [Fact]
    public void Provider_LoadsDnd5eBackgrounds_FromEmbeddedResources()
    {
        var backgrounds = Services.Backgrounds.GetBackgroundsForSystem(RulesetSystem.Dnd5e);

        Assert.True(backgrounds.Count >= 5);
        Assert.True(backgrounds.ContainsKey("acolyte"));
        Assert.True(backgrounds.ContainsKey("criminal"));
    }

    [Fact]
    public void InitializePools_Dnd5eCharacterWithFeat_GrantsFeatPools()
    {
        var initializer = CreateInitializerWithDiskFeatPool(
            "dnd5e",
            featName: "magic_initiate",
            poolName: "magic_initiate_uses",
            system: RulesetSystem.Dnd5e);

        var character = new Character
        {
            Id = "chars/test_mi",
            Name = "Initiate",
            ClassLevel = "Fighter 3",
            SystemStats = new Dnd5eExtension
            {
                Level = 3,
                ClassLevels = [new ClassLevelEntry { Class = "Fighter", Level = 3 }],
                Feats = ["magic_initiate"],
            },
        };

        initializer.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.True(character.SystemStats.ResourcePools.TryGetValue("magic_initiate_uses", out var pool));
        Assert.Equal(2, pool.Max);
    }

    [Fact]
    public void InitializePools_Pf2eCharacterWithClassFeat_GrantsFeatPools()
    {
        var initializer = CreateInitializerWithDiskFeatPool(
            "pf2e",
            featName: "diehard",
            poolName: "diehard_reserve",
            system: RulesetSystem.Pathfinder2e,
            defaultMax: 1);

        var character = new Character
        {
            Id = "chars/test_diehard",
            Name = "Diehard Fighter",
            ClassLevel = "Fighter 5",
            SystemStats = new Pf2eExtension
            {
                Level = 5,
                ClassFeats = ["diehard"],
            },
        };

        initializer.InitializePools(character, RulesetSystem.Pathfinder2e, null);

        Assert.True(character.SystemStats.ResourcePools.TryGetValue("diehard_reserve", out var pool));
        Assert.Equal(1, pool.Max);
    }

    [Fact]
    public void FeatDefinition_Merge_ExtraPools_ChildWins()
    {
        var parent = new FeatDefinition { Name = "base_feat", ExtraPools = ["pool_a"] };
        var child = new FeatDefinition { Name = "sub_feat", Inherits = ["base_feat"], ExtraPools = ["pool_b"] };

        var merged = FeatDefinition.Merge(child, parent);

        Assert.Equal(["pool_b"], merged.ExtraPools);
    }

    [Fact]
    public void InitializePools_FeatGrantedPool_RestrictedToClassCharacterLacks_IsNotGranted()
    {
        var initializer = CreateInitializerWithDiskFeatPool(
            "dnd5e",
            featName: "martial_focus",
            poolName: "martial_focus_points",
            system: RulesetSystem.Dnd5e,
            applicableClasses: ["wizard"]);

        var character = new Character
        {
            Id = "chars/test_wrong_class",
            Name = "Non-Wizard",
            ClassLevel = "Fighter 5",
            SystemStats = new Dnd5eExtension
            {
                Level = 5,
                ClassLevels = [new ClassLevelEntry { Class = "Fighter", Level = 5 }],
                Feats = ["martial_focus"],
            },
        };

        initializer.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.False(character.SystemStats.ResourcePools.ContainsKey("martial_focus_points"));
    }

    [Fact]
    public void InitializePools_FeatGrantedPool_UsesGrantingClassLevel_NotTotalCharacterLevel()
    {
        var initializer = CreateInitializerWithDiskFeatPool(
            "dnd5e",
            featName: "martial_focus",
            poolName: "martial_focus_points",
            system: RulesetSystem.Dnd5e,
            applicableClasses: ["fighter"],
            levelToMaxMap: new Dictionary<int, int> { [1] = 1, [3] = 2 });

        // Total character level (8) differs from the fighter class level (3): if the fix didn't
        // route feat-granted pools through class-level resolution, this would incorrectly use 8.
        var character = new Character
        {
            Id = "chars/test_multiclass",
            Name = "Fighter/Rogue",
            ClassLevel = "Fighter 3 / Rogue 5",
            SystemStats = new Dnd5eExtension
            {
                Level = 8,
                ClassLevels =
                [
                    new ClassLevelEntry { Class = "Fighter", Level = 3 },
                    new ClassLevelEntry { Class = "Rogue", Level = 5 },
                ],
                Feats = ["martial_focus"],
            },
        };

        initializer.InitializePools(character, RulesetSystem.Dnd5e, null);

        Assert.True(character.SystemStats.ResourcePools.TryGetValue("martial_focus_points", out var pool));
        Assert.Equal(2, pool.Max);
    }

    private static ResourcePoolInitializer CreateInitializerWithDiskFeatPool(
        string systemSlug,
        string featName,
        string poolName,
        RulesetSystem system,
        int defaultMax = 2,
        IReadOnlyList<string>? applicableClasses = null,
        IReadOnlyDictionary<int, int>? levelToMaxMap = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_featpool_" + Guid.NewGuid());
        var assembly = typeof(ResourcePoolProvider).Assembly;

        var poolsDir = Path.Combine(dir, systemSlug, "pools");
        var featsDir = Path.Combine(dir, systemSlug, "feats");
        Directory.CreateDirectory(poolsDir);
        Directory.CreateDirectory(featsDir);

        File.WriteAllText(
            Path.Combine(featsDir, $"{featName}.yaml"),
            $"""
            name: {featName}
            system: {systemSlug}
            extraPools: [{poolName}]
            """);

        var poolYaml = $"""
            name: {poolName}
            applicableSystems: [{systemSlug}]
            featGrantedOnly: true
            recovery: LongRest
            """;

        if (applicableClasses is { Count: > 0 })
        {
            poolYaml += $"\napplicableClasses: [{string.Join(", ", applicableClasses)}]";
        }

        if (levelToMaxMap is { Count: > 0 })
        {
            poolYaml += "\nlevelToMaxMap:\n" + string.Join(
                "\n",
                levelToMaxMap.Select(kvp => $"  \"{kvp.Key}\": {kvp.Value}"));
        }
        else
        {
            poolYaml += $"\ndefaultMax: {defaultMax}";
        }

        File.WriteAllText(Path.Combine(poolsDir, $"{poolName}.yaml"), poolYaml);

        var pools = new ResourcePoolProvider(dir, assembly);
        var classes = new ClassDefinitionProvider(dir, assembly);
        var feats = new FeatDefinitionProvider(dir, assembly);
        return new ResourcePoolInitializer(pools, classes, feats);
    }
}