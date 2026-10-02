using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>A plugin adds a subclass in a file of its own, hides a shipped one, and the builder tags it homebrew.</summary>
[Collection("PluginStatics")]
public class ClassOptionTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(ProgressionDefinitionProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-class-options-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string folder, string file, string yaml)
    {
        var dir = Path.Combine(_root, "plugin", "dnd5e", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), yaml);
    }

    private ProgressionDefinitionProvider Provider() =>
        new(Path.Combine(_root, "host"), Asm, null, [Path.Combine(_root, "plugin")]);

    private const string EmberKnight = """
        name: emberKnight
        class: fighter
        label: Ember Knight
        description: A knight of the kindled blade.
        features:
          3:
            - name: Kindled Blade
              description: Your weapon burns.
              effects:
                - { kind: damageBonus, value: 1, weapon: [melee] }
          7:
            - name: Banked Coals
        """;

    [Fact]
    public void ClassOptionFile_JoinsTheSubclassChoice_AndIsTaggedHomebrew()
    {
        Write("classOptions", "ember_knight.yaml", EmberKnight);
        Provider().TryGetProgression("dnd5e", "fighter", out var fighter);

        var choice = fighter!.Levels[3].Choices.Single(c => c.Key == "subclass");
        var options = fighter.OptionsFor(choice);

        Assert.Contains(options, o => o.Id == "champion" && !o.Homebrew);
        var ember = Assert.Single(options, o => o.Id == "emberKnight");
        Assert.True(ember.Homebrew);
        Assert.Equal("Ember Knight", ember.Label);

        // Picked, its features arrive by class level without the plugin restating level 3.
        var gained = fighter.FeaturesUpTo(7, (level, key) => key == "subclass" ? ["emberKnight"] : []);
        Assert.Contains(gained, f => f.Level == 3 && f.Feature.Name == "Kindled Blade" && f.From?.Id == "emberKnight");
        Assert.Contains(gained, f => f.Level == 7 && f.Feature.Name == "Banked Coals");
        Assert.Contains(gained.SelectMany(f => f.Feature.Effects), e => e.Value == 1);
    }

    [Fact]
    public void ClassOptionForAnAlias_AndForAMissingClass()
    {
        Write("classOptions", "a.yaml", EmberKnight.Replace("class: fighter", "class: nosuchclass"));
        Provider().TryGetProgression("dnd5e", "fighter", out var fighter);

        var choice = fighter!.Levels[3].Choices.Single(c => c.Key == "subclass");
        Assert.DoesNotContain(fighter.OptionsFor(choice), o => o.Id == "emberKnight");
    }

    [Fact]
    public void HideOptions_TakesAShippedSubclassOut_ButNotItsRecord()
    {
        Write("progressions", "hide.yaml", "patches: fighter\nhideOptions+: [champion]\n");
        Write("classOptions", "ember_knight.yaml", EmberKnight);
        Provider().TryGetProgression("dnd5e", "fighter", out var fighter);

        var choice = fighter!.Levels[3].Choices.Single(c => c.Key == "subclass");
        var ids = fighter.OptionsFor(choice).Select(o => o.Id).ToList();

        Assert.Equal(["emberKnight"], ids);
        // A character that picked it earlier no longer gets its features.
        Assert.DoesNotContain(fighter.FeaturesUpTo(10, (_, key) => key == "subclass" ? ["champion"] : []), f => f.From?.Id == "champion");
    }

    [Fact]
    public void HiddenTemplate_LeavesEveryList()
    {
        Write("backgrounds", "hide_acolyte.yaml", "patches: acolyte\nhidden: true\n");
        var layers = new RulesetContentLayers<BackgroundDefinition>(
            Path.Combine(_root, "host"), Asm, ["backgrounds"], BackgroundDefinition.Merge, null, [Path.Combine(_root, "plugin")]);

        Assert.DoesNotContain("acolyte", layers.Resolve("dnd5e").Keys);
    }

    [Fact]
    public void BuilderSlot_CarriesTheHomebrewTag()
    {
        Write("classOptions", "ember_knight.yaml", EmberKnight);
        Provider().TryGetProgression("dnd5e", "fighter", out var fighter);

        var slot = LevelChoiceSlots.For(fighter!, 3, [], "levelChoices").Single(s => s.Key == "subclass");

        Assert.True(slot.Options.Single(o => o.Id == "emberKnight").Homebrew);
        Assert.False(slot.Options.Single(o => o.Id == "champion").Homebrew);
    }
}
