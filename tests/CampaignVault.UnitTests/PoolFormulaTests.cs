using System;
using System.Collections.Generic;
using System.IO;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Resource pools beyond a level table: a maximum from the character (<c>maxFrom</c>), a die and recovery that change by
/// level, and pools a class feature grants by name (a subclass's).
/// </summary>
public class PoolFormulaTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(ResourcePoolProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-pool-formula-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Character Bard(int level, int charisma) => new()
    {
        Id = "chars/bard", Name = "Bard", ClassLevel = $"Bard {level}",
        SystemStats = new Dnd5eExtension { Level = level, Charisma = charisma },
    };

    [Fact]
    public void BardicInspiration_UsesCharismaModifier_WithItsDieAndRecovery()
    {
        var bard = Bard(1, 16);

        RulesetDataTestHelper.CreateServices().Initializer.InitializePools(bard, RulesetSystem.Dnd5e, null);

        var pool = bard.SystemStats!.ResourcePools["bardic_inspiration"];
        Assert.Equal((3, "d6", RecoveryType.LongRest), (pool.Max, pool.Die, pool.Recovery));
    }

    [Fact]
    public void BardicInspiration_AtLevel5_IsAtLeastOne_BiggerDie_ShortRest()
    {
        var bard = Bard(5, 8);

        RulesetDataTestHelper.CreateServices().Initializer.InitializePools(bard, RulesetSystem.Dnd5e, null);

        var pool = bard.SystemStats!.ResourcePools["bardic_inspiration"];
        Assert.Equal((1, "d8", RecoveryType.ShortRest), (pool.Max, pool.Die, pool.Recovery));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void KiPoints_StartAtSecondLevel_EqualToMonkLevel(int level, int expected)
    {
        var monk = new Character
        {
            Id = "chars/monk", Name = "Monk", ClassLevel = $"Monk {level}",
            SystemStats = new Dnd5eExtension { Level = level },
        };

        RulesetDataTestHelper.CreateServices().Initializer.InitializePools(monk, RulesetSystem.Dnd5e, null);

        Assert.Equal(expected, monk.SystemStats!.ResourcePools.TryGetValue("ki_points", out var ki) ? ki.Max : 0);
    }

    [Fact]
    public void MaxFrom_LevelMultiplierAndProficiency_AddUp()
    {
        var character = new Character
        {
            Id = "chars/w", Name = "W", ClassLevel = "Wizard 5",
            SystemStats = new Dnd5eExtension { Level = 5, Intelligence = 16 },
        };
        var config = new CampaignConfig
        {
            ResourcePoolSchemas = new Dictionary<string, ResourcePoolTemplate>
            {
                ["ward"] = new() { ApplicableClasses = ["wizard"], MaxFrom = new PoolMaxFormula { LevelMultiplier = 2, Ability = "Intelligence" } },
                ["knacks"] = new() { MaxFrom = new PoolMaxFormula { ProficiencyBonus = true } },
            },
        };

        RulesetDataTestHelper.CreateServices().Initializer.InitializePools(character, RulesetSystem.Dnd5e, config);

        Assert.Equal(13, character.SystemStats!.ResourcePools["ward"].Max);   // 2 × 5 + 3
        Assert.Equal(3, character.SystemStats.ResourcePools["knacks"].Max);   // proficiency bonus at level 5
    }

    [Fact]
    public void GrantedOnlyPool_ComesFromAPickedOptionsFeature_NotFromTheClass()
    {
        var dir = Path.Combine(_root, "testsys", "progressions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "duelist.yaml"), """
            name: duelist
            system: testsys
            levels:
              3:
                features:
                  - name: School
                    choices:
                      subclass:
                        type: Enum
                        options:
                          - id: tactician
                            label: Tactician
                            features:
                              3: [{ name: Gambits, pools: [gambit_dice] }]
                          - id: brawler
                            label: Brawler
            """);
        var progressions = new ProgressionDefinitionProvider(_root, Asm);
        var services = RulesetDataTestHelper.CreateServices();
        var initializer = new ResourcePoolInitializer(services.Pools, services.Classes, services.Feats, progressions);
        var config = new CampaignConfig
        {
            ResourcePoolSchemas = new Dictionary<string, ResourcePoolTemplate>
            {
                ["gambit_dice"] = new()
                {
                    GrantedOnly = true, ApplicableClasses = ["duelist"], Recovery = RecoveryType.ShortRest,
                    LevelToMaxMap = new() { ["3"] = 4, ["7"] = 5 }, Die = "d8", DieByLevel = new() { ["10"] = "d10" },
                },
            },
        };
        Character Duelist(string pick, int level) => new()
        {
            Id = "chars/d", Name = "D", ClassLevel = $"Duelist {level}",
            SystemStats = new Dnd5eExtension
            {
                Level = level,
                LevelUpChoices = [new LevelUpChoiceRecord { Level = 3, Key = "subclass", Value = pick }],
            },
        };

        var tactician = Duelist("tactician", 10);
        initializer.InitializePools(tactician, "testsys", config);
        var brawler = Duelist("brawler", 10);
        initializer.InitializePools(brawler, "testsys", config);

        var pool = tactician.SystemStats!.ResourcePools["gambit_dice"];
        Assert.Equal((5, "d10"), (pool.Max, pool.Die));
        Assert.False(brawler.SystemStats!.ResourcePools.ContainsKey("gambit_dice"));
    }
}
