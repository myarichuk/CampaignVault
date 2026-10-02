using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.UnitTests;

/// <summary>Effects with no action of their own: initiative, speed and passive bonuses, and flat damage reduction.</summary>
public sealed class EffectKindsTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(FeatDefinitionProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-effect-kinds-" + Guid.NewGuid().ToString("N"));

    public EffectKindsTests()
    {
        var dir = Path.Combine(_root, "dnd5e", "feats");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "watchful.yaml"), """
            name: watchful
            system: dnd5e
            effects:
              - { kind: initiativeBonus, value: 5 }
              - { kind: speedBonus, value: 10 }
              - { kind: speedBonus, value: 5, assert: [downhill], when: "running downhill" }
              - { kind: passiveBonus, value: 5 }
              - { kind: passiveBonus, value: 5, subject: Investigation }
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Character Watchful() => new()
    {
        Id = "chars/w", Name = "W", ClassLevel = "Fighter 1",
        SystemStats = new Dnd5eExtension { Level = 1, Wisdom = 14, Intelligence = 12, Feats = ["watchful"] },
    };

    [Fact]
    public void TheNewKinds_Validate()
    {
        Assert.Empty(FeatEffectRules.Validate(
        [
            new FeatEffect { Kind = "initiativeBonus", Value = 5 }, new FeatEffect { Kind = "speedBonus", Value = 10 },
            new FeatEffect { Kind = "passiveBonus", Value = 5, Subject = "Investigation" },
            new FeatEffect { Kind = "damageReduction", Value = 3, DamageType = "bludgeoning,piercing,slashing" },
        ]));
        Assert.Single(FeatEffectRules.Validate([new FeatEffect { Kind = "passiveBonus", Value = 5, Subject = "Athletics" }]));
    }

    [Fact]
    public void InitiativeAndSpeed_AddTheUnconditionalBonuses()
    {
        var provider = new CharacterEffectModifierProvider(new FeatDefinitionProvider(_root, Asm));
        RollQuery Query(string kind) => new(kind, null, [], Watchful(), null, RulesetSystem.Dnd5e, new Dictionary<string, string>());

        Assert.Equal(5, Assert.Single(provider.Modifiers(Query(RollKinds.Initiative))).Bonus);
        Assert.Equal(10, Assert.Single(provider.Modifiers(Query(RollKinds.Speed))).Bonus);
        Assert.Empty(provider.Modifiers(Query(RollKinds.Save)));
    }

    [Fact]
    public async Task PassiveScores_AddTheirBonuses()
    {
        var watchful = Watchful();

        await new Dnd5eDerivePassivePerceptionStep(new FeatDefinitionProvider(_root, Asm))
            .ApplyAsync(new BootstrapContext { Character = watchful, ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);

        var attributes = ((Dnd5eExtension)watchful.SystemStats!).Attributes;
        Assert.Equal(17, attributes["passivePerception"]);      // 10 + 2 Wisdom + 5
        Assert.Equal(16, attributes["passiveInvestigation"]);   // 10 + 1 Intelligence + 5
    }

    [Theory]
    [InlineData("slashing", "nonmagical", 3)]
    [InlineData("slashing", "", 0)]       // the DM didn't claim the weapon is nonmagical
    [InlineData("fire", "nonmagical", 0)] // not a type it reduces
    public void DamageReduction_ByTypeAndAssertion(string damageType, string assert, int expected)
    {
        ActiveFeatEffect[] effects =
        [
            new("Armored", new FeatEffect
            {
                Kind = "damageReduction", Value = 3, DamageType = "bludgeoning, piercing, slashing",
                Assert = ["nonmagical"], When = "the weapon is nonmagical",
            }),
        ];
        var attack = new RulesetAction { ActionType = RulesetActionType.Attack, ActionName = "Attack", CharacterId = "chars/a" };
        if (assert.Length > 0) attack.Parameters["assert"] = assert;
        var notes = new List<string>();

        Assert.Equal(expected, FeatEffectRules.DamageReduction(effects, damageType, attack, notes));
        Assert.Equal(expected > 0 || damageType == "slashing", notes.Count > 0);
    }
}
