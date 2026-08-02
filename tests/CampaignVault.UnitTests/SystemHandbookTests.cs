using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class SystemHandbookTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private static readonly Assembly Assembly = typeof(ClassDefinitionProvider).Assembly;

    public SystemHandbookTests(RavenDBFixture fixture) => _fixture = fixture;

    [Fact]
    public void GetSystemHandbook_Dnd5e_ReturnsKnownClasses()
    {
        var handbook = BuildHandbook(RulesetSystem.Dnd5e);

        Assert.Equal("dnd5e", handbook.System);
        Assert.Contains(handbook.Classes, c => c.Name == "fighter");
        Assert.Contains(handbook.Classes, c => c.Name == "wizard");

        var fighter = handbook.Classes.First(c => c.Name == "fighter");
        Assert.Equal("None", fighter.CasterType);
        Assert.Equal("d10", fighter.HitDie);
        Assert.Contains("action_surge", fighter.Pools);
    }

    [Fact]
    public void GetSystemHandbook_Dnd5e_IncludesIdentityAndConditions()
    {
        var handbook = BuildHandbook(RulesetSystem.Dnd5e);

        Assert.Contains(handbook.Races, r => r == "elf");
        Assert.Contains(handbook.Backgrounds, b => b == "acolyte");
        Assert.Contains(handbook.Feats, f => f == "lucky");
        Assert.Contains(handbook.Conditions, c => c.Name == "frightened");
        Assert.Contains(SystemHandbookBuilder.SpellDiscoveryNote, handbook.Notes);
        Assert.Contains("SRD 5.1", handbook.Notes);
    }

    [Fact]
    public void GetSystemHandbook_IncludesHomebrew_WhenYamlPresentOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_handbook_hb_" + Guid.NewGuid());
        var classesDir = Path.Combine(dir, "dnd5e", "classes");
        Directory.CreateDirectory(classesDir);

        File.WriteAllText(
            Path.Combine(classesDir, "homebrew_warlord.yaml"),
            """
            name: homebrew_warlord
            system: dnd5e
            hitDie: d8
            casterType: None
            pools: [command_points]
            aliases: [warlord, homebrew warlord]
            """);

        var handbook = BuildHandbook(RulesetSystem.Dnd5e, dir);

        Assert.Contains(handbook.Classes, c => c.Name == "homebrew_warlord");
        Assert.Contains(handbook.Classes.First(c => c.Name == "homebrew_warlord").Pools, p => p == "command_points");
    }

    [Fact]
    public void GetSystemHandbook_Pf2e_IncludesMartialClassesAndCoverageNote()
    {
        var handbook = BuildHandbook(RulesetSystem.Pathfinder2e);

        Assert.Contains(handbook.Classes, c => c.Name == "fighter");
        Assert.Contains(handbook.Classes, c => c.Name == "ranger");
        Assert.Contains(handbook.Classes, c => c.Name == "rogue");
        Assert.True(handbook.Notes.Contains("ORC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetSpells_FiltersByClassAndLevel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cv_handbook_spells_" + Guid.NewGuid());
        var spells = new SpellDefinitionProvider(dir, Assembly);
        var classes = new ClassDefinitionProvider(dir, Assembly);

        var level3Wizard = spells.QuerySpells(RulesetSystem.Dnd5e, "Wizard", 3, classes);

        Assert.Contains(level3Wizard, s => s.Name == "fireball");
        Assert.DoesNotContain(level3Wizard, s => s.Name == "magic_missile");
        Assert.All(level3Wizard, s => Assert.Equal(3, s.Level));
    }

    [Fact]
    public void SpellQueryBuilder_BuildHint_SuggestsLevelFilterForLargeLists()
    {
        var page = SpellQueryBuilder.QueryPage(
            new SpellDefinitionProvider(
                Path.Combine(Path.GetTempPath(), "cv_hint_" + Guid.NewGuid()),
                Assembly),
            RulesetSystem.Dnd5e,
            "Wizard",
            new ClassDefinitionProvider(
                Path.Combine(Path.GetTempPath(), "cv_hint_cls_" + Guid.NewGuid()),
                Assembly));

        var hint = SpellQueryBuilder.BuildHint(page, "Wizard", level: null);

        Assert.Contains("level=", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSystemHandbook_Tool_ReturnsHandbookForCampaign()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await tools.CreateCampaign("handbook-test", RulesetSystem.Dnd5e);

        var result = await tools.GetSystemHandbook("handbook-test");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("dnd5e", result.Data.System);
        Assert.True(result.Data.Classes.Count > 0);
    }

    private static SystemHandbookResponse BuildHandbook(
        string system,
        string? rulesetDataDir = null)
    {
        var dir = rulesetDataDir ?? Path.Combine(Path.GetTempPath(), "cv_handbook_" + Guid.NewGuid());
        return SystemHandbookBuilder.Build(
            system,
            new ClassDefinitionProvider(dir, Assembly),
            new RaceDefinitionProvider(dir, Assembly),
            new BackgroundDefinitionProvider(dir, Assembly),
            new FeatDefinitionProvider(dir, Assembly),
            new ConditionDefinitionProvider(dir, Assembly),
            new CreatureDefinitionProvider(dir, Assembly));
    }
}