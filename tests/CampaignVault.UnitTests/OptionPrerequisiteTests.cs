using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Templates;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>An option with <c>prerequisite</c> is offered only once the class level and the earlier pick it names are reached.</summary>
public class OptionPrerequisiteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-option-prerequisite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private ProgressionDefinition Hexer()
    {
        var dir = Path.Combine(_root, "testsys", "progressions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hexer.yaml"), """
            name: hexer
            system: testsys
            levels:
              2:
                features:
                  - name: Rites
                    choices:
                      rite:
                        type: FeatSelection
                        count: 2
                        options:
                          - candleSight
                          - { id: ashStep, label: Ash Step }
                          - { id: emberTongue, label: Ember Tongue, prerequisite: { level: 5 } }
                          - { id: boundLantern, label: Bound Lantern, prerequisite: { level: 5, option: lanternPact } }
              3:
                features:
                  - name: Pact
                    choices:
                      pact:
                        type: Enum
                        options:
                          - { id: lanternPact, label: Lantern Pact }
                          - { id: mirrorPact, label: Mirror Pact }
              5:
                features:
                  - name: Rites
                    choices:
                      rite: { type: FeatSelection }
            """);
        return new ProgressionDefinitionProvider(_root, typeof(ProgressionDefinitionProvider).Assembly)
            .GetProgressionsForSystem("testsys")["hexer"];
    }

    private static IReadOnlyList<string> Offered(ProgressionDefinition progression, int level, string slot,
        Func<int, string, IEnumerable<string>> picked) =>
        [.. LevelChoiceSlots.For(progression, level, [], "levelChoices", picked: picked).Single(s => s.Id == slot).Options.Select(o => o.Id)];

    [Fact]
    public void Prerequisite_IsReadFromYaml()
    {
        var option = Hexer().Levels[2].Choices.Single().Options.Single(o => o.Id == "boundLantern");

        Assert.Equal(5, option.Prerequisite!.Level);
        Assert.Equal("lanternPact", option.Prerequisite.Option);
    }

    [Fact]
    public void ScalarOption_IsItsOwnIdAndLabel_WithNoPrerequisite()
    {
        var option = Hexer().Levels[2].Choices.Single().Options.Single(o => o.Id == "candleSight");

        Assert.Equal("candleSight", option.Label);
        Assert.Null(option.Prerequisite);
    }

    [Fact]
    public void BelowTheLevel_TheOptionIsNotOffered()
    {
        var offered = Offered(Hexer(), 5, "2.rite", (_, _) => []);

        Assert.Equal(["candleSight", "ashStep"], offered);
    }

    [Fact]
    public void AtTheLevel_WithoutTheNamedPick_OnlyTheLevelGateOpens()
    {
        var offered = Offered(Hexer(), 5, "5.rite", (level, key) => (level, key) == (3, "pact") ? ["mirrorPact"] : []);

        Assert.Contains("emberTongue", offered);
        Assert.DoesNotContain("boundLantern", offered);
    }

    [Fact]
    public void AtTheLevel_WithTheNamedPick_BothGatesOpen()
    {
        var offered = Offered(Hexer(), 5, "5.rite", (level, key) => (level, key) == (3, "pact") ? ["LANTERNPACT"] : []);

        Assert.Contains("emberTongue", offered);
        Assert.Contains("boundLantern", offered);
    }

    [Fact]
    public void ClassOption_CarriesItsPrerequisite()
    {
        var option = new ClassOptionDefinition
        {
            Name = "gloomRite",
            Class = "hexer",
            Choice = "rite",
            Prerequisite = new OptionPrerequisite { Level = 7 },
        }.ToOption();

        Assert.False(option.Prerequisite!.MetBy(6, []));
        Assert.True(option.Prerequisite.MetBy(7, []));
    }

    [Fact]
    public void CoreWarlock_OffersPactGatedInvocationsOnlyWithThePact()
    {
        var warlock = new ProgressionDefinitionProvider(_root, typeof(ProgressionDefinitionProvider).Assembly)
            .GetProgressionsForSystem("dnd5e")["warlock"];
        Func<int, string, IEnumerable<string>> blade = (level, key) => (level, key) == (3, "pactBoon") ? ["blade"] : [];

        Assert.Equal(32, warlock.Levels[2].Choices.Single(c => c.Key == "invocation").Options.Count);
        Assert.DoesNotContain("thirstingBlade", Offered(warlock, 5, "2.invocation", blade));
        Assert.Contains("thirstingBlade", Offered(warlock, 5, "5.invocation", blade));
        Assert.DoesNotContain("lifedrinker", Offered(warlock, 5, "5.invocation", blade));
        Assert.DoesNotContain("thirstingBlade", Offered(warlock, 5, "5.invocation", (_, _) => []));
    }
}
