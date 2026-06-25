using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using Xunit;

namespace CampaignVault.Tests;

public class Dnd5eMulticlassTests
{
    [Fact]
    public void ParseClassLevels_FromStructuredList_ReturnsEntries()
    {
        var entries = Dnd5eClassProfileResolver.ParseClassLevels(
            null,
            [new ClassLevelEntry { Class = "Fighter", Level = 5 }, new ClassLevelEntry { Class = "Wizard", Level = 5 }]);

        Assert.Equal(2, entries.Count);
        Assert.Equal(10, Dnd5eClassProfileResolver.TotalLevel(entries));
    }

    [Fact]
    public void ParseClassLevels_FromFreeformString_ParsesMulticlass()
    {
        var entries = Dnd5eClassProfileResolver.ParseClassLevels("Human Fighter 5 / Wizard 5", null);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Fighter", entries[0].Class, ignoreCase: true);
        Assert.Equal(5, entries[0].Level);
        Assert.Equal("Wizard", entries[1].Class, ignoreCase: true);
        Assert.Equal(5, entries[1].Level);
    }

    [Fact]
    public async Task MulticlassHp_Fighter5Wizard5_UsesPerClassHitDice()
    {
        var step = new Dnd5eDeriveHitPointsStep(new DefaultRollService(new System.Random(42)));
        var character = new Character
        {
            Id = "chars/gish",
            Name = "Gish",
            ClassLevel = "Fighter 5 / Wizard 5",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 16,
                ClassLevels =
                [
                    new ClassLevelEntry { Class = "Fighter", Level = 5 },
                    new ClassLevelEntry { Class = "Wizard", Level = 5 },
                ],
            },
        };

        var result = await step.ApplyAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        Assert.NotNull(result);
        Assert.Contains("multiclass", result!.Message);
        // CON 16 (+3): Fighter L1 max d10+3=13; Fighter L2-5: 4×(6+3)=36; Wizard L1-5: 5×(4+3)=35 → 84
        Assert.Equal(84, character.MaxHp);
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
        Assert.Equal(10, stats.Level);
    }
}
