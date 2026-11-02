using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>A campaign's homebrew subclasses, ancestries and powers: saved with the campaign, offered in its builder only.</summary>
[Collection("RavenDB")]
public class HomebrewTemplateTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private const string EmberKnight = "name: ember_knight\nclass: fighter\nlabel: Ember Knight\nfeatures:\n  3:\n    - { name: Kindled Blade, description: The blade burns. }\n";

    private static string Slug() => "homebrew-" + Guid.NewGuid().ToString("N")[..8];

    private static CharacterDraft Fighter(int level = 3) =>
        new CharacterDraft { Kind = "pc", Level = level }.With("race", "human").With("class", "fighter");

    private static WorldBuildBatch Batch(params HomebrewTemplateUpsertRequest[] templates) => new() { Homebrew = [.. templates] };

    private async Task<string[]> SubclassOptions(string slug, bool homebrewOnly = false)
    {
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var result = await builder.CharacterBuilder("options", Fighter(), slug, "levels");
        Assert.True(result.Success, result.Summary);
        var subclass = result.Data!.Slots!.Single(s => s.Key == "subclass");
        return [.. subclass.Options.Where(o => !homebrewOnly || o.Homebrew).Select(o => o.Id)];
    }

    [Fact]
    public async Task ASubclass_IsOfferedTaggedHomebrew_ToItsCampaignOnly()
    {
        var mine = Slug();
        var other = Slug();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);

        var saved = await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest { Kind = "classOption", System = "dnd5e", Yaml = EmberKnight }), mine);
        Assert.True(saved.Success, saved.Summary);
        Assert.Equal(1, saved.Data!.Kinds["homebrew"].Created);

        Assert.Equal(["ember_knight"], await SubclassOptions(mine, homebrewOnly: true));
        Assert.Empty(await SubclassOptions(other, homebrewOnly: true));
        Assert.Contains("champion", await SubclassOptions(mine));
    }

    [Fact]
    public async Task SavingTheSameNameAgain_Replaces_AndArchivingStopsOfferingIt()
    {
        var slug = Slug();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest { Kind = "classOption", System = "dnd5e", Yaml = EmberKnight }), slug);

        var again = await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest
        {
            Kind = "classOption", System = "dnd5e", Yaml = EmberKnight.Replace("Ember Knight", "Cinder Knight"),
        }), slug);
        Assert.Equal(1, again.Data!.Kinds["homebrew"].Updated);

        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var slots = (await builder.CharacterBuilder("options", Fighter(), slug, "levels")).Data!.Slots!;
        Assert.Equal("Cinder Knight", slots.Single(s => s.Key == "subclass").Options.Single(o => o.Id == "ember_knight").Label);

        await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest
        {
            Kind = "classOption", System = "dnd5e", Yaml = EmberKnight, IsArchived = true,
        }), slug);
        Assert.Empty(await SubclassOptions(slug, homebrewOnly: true));
    }

    [Theory]
    [InlineData("classOption", "name: orphan\nclass: not_a_class\n", "No dnd5e class matches")]
    [InlineData("classOption", "name: orphan\n", "needs 'class:'")]
    [InlineData("power", "name: orphan\ntype: toaster\n", "deity, patron or lineage")]
    [InlineData("ancestry", "description: nameless\n", "needs a 'name:'")]
    [InlineData("monster", "name: orphan\n", "Unknown kind")]
    [InlineData("classOption", "name: [unclosed\n", "doesn't read")]
    public async Task ABadTemplate_IsRefused_WithTheReason(string kind, string yaml, string reason)
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);

        var result = await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest { Kind = kind, System = "dnd5e", Yaml = yaml }), Slug());

        Assert.False(result.Success);
        Assert.Contains(reason, result.Summary);
    }

    private const string Smuggler =
        "name: tidewater_smuggler\nlabel: Tidewater Smuggler\ndescription: You ran untaxed cargo through the reed channels.\n"
        + "skillProficiencies: [Stealth, Deception]\ntoolProficiencies: [Navigator's tools]\nlanguages: [One of your choice]\n"
        + "feature: Channel Friends\nequipment:\n  - { name: Oilskin coat }\n  - { item: dagger }\ngold: 10\n";

    [Fact]
    public async Task ABackground_IsOfferedBesideTheShippedOnes_AndItsGrantsLandOnTheCharacter()
    {
        var slug = Slug();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        var saved = await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest { Kind = "background", System = "dnd5e", Yaml = Smuggler }), slug);
        Assert.True(saved.Success, saved.Summary);

        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var backgrounds = (await builder.CharacterBuilder("options", new CharacterDraft { Kind = "pc" }, slug, "background")).Data!.Options!;
        Assert.True(backgrounds.Single(o => o.Id == "tidewater_smuggler").Homebrew);
        Assert.False(backgrounds.Single(o => o.Id == "acolyte").Homebrew, "the shipped background stays");
        var elsewhere = (await builder.CharacterBuilder("options", new CharacterDraft { Kind = "pc" }, Slug(), "background")).Data!.Options!;
        Assert.DoesNotContain(elsewhere, o => o.Id == "tidewater_smuggler");

        var draft = CharacterCreationTests.Wizard().With("background", "tidewater_smuggler");
        var preview = await builder.CharacterBuilder("preview", draft, slug);
        Assert.True(preview.Success, preview.Summary);
        Assert.DoesNotContain(preview.Data!.Errors, e => e.Step == "background");
        var stats = Assert.IsType<CampaignVault.Models.Dnd5eExtension>(preview.Data.Character!.SystemStats);
        Assert.Equal("tidewater_smuggler", stats.Background);
        Assert.Contains("Stealth", stats.SkillModifiers.Keys);
        Assert.Contains("Deception", stats.SkillModifiers.Keys);
        Assert.Equal(10, stats.ResourcePools["gold"].Current);
    }

    [Theory]
    [InlineData("dnd5e", "skillProficiencies: [Stealth, Deception, Arcana]\n", "exactly 2 skill proficiencies")]
    [InlineData("dnd5e", "skillProficiencies: [Stealth, Smuggling]\n", "'Smuggling' is not a dnd5e skill")]
    [InlineData("dnd5e", "skillProficiencies: [Stealth, Deception]\ntoolProficiencies: [Thieves' tools, Navigator's tools]\nlanguages: [Elvish]\n", "at most 2 tool proficiencies and languages")]
    [InlineData("dnd5e", "skillProficiencies: [Stealth, Deception]\ngold: 200\n", "0 to 25 gp")]
    [InlineData("dnd5e", "skillProficiencies: [Stealth, Deception]\nequipment:\n  - { item: not_an_item_at_all }\n", "No item template 'not_an_item_at_all'")]
    [InlineData("dnd5e", "skillProficiencies: [Stealth, Deception]\nskillFeat: assurance\n", "pf2e fields")]
    [InlineData("pf2e", "boosts: [Dexterity, Charisma]\nskillProficiencies: [Stealth, Deception]\nlore: Smuggling Lore\nskillFeat: experienced_smuggler\n", "trains one skill")]
    [InlineData("pf2e", "boosts: [Dexterity]\nskillProficiencies: [Stealth]\nlore: Smuggling Lore\nskillFeat: experienced_smuggler\n", "two different abilities")]
    [InlineData("pf2e", "boosts: [Dexterity, Charisma]\nskillProficiencies: [Stealth]\nskillFeat: experienced_smuggler\n", "set lore")]
    [InlineData("pf2e", "boosts: [Dexterity, Charisma]\nskillProficiencies: [Stealth]\nlore: Smuggling Lore\n", "set skillFeat")]
    public async Task ABackgroundOutsideTheRulesetsBudget_IsRefused_WithTheReason(string system, string fields, string reason)
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        var yaml = "name: overloaded\ndescription: Too much.\n" + fields;

        var result = await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest { Kind = "background", System = system, Yaml = yaml }), Slug());

        Assert.False(result.Success);
        Assert.Contains(reason, result.Summary);
    }

    [Fact]
    public void APf2eBackground_InsideTheBudget_HasNoProblems()
    {
        var background = CampaignVault.Data.Templates.HomebrewTemplates.ReadBackground(
            "name: river_pilot\ndescription: You steered barges.\nboosts: [Dexterity, Wisdom]\nskillOptions: [Athletics, Survival]\nlore: River Lore\nskillFeat: assurance\n")!;

        Assert.Empty(CampaignVault.Data.Templates.HomebrewTemplates.BackgroundProblems(background, "pf2e"));
    }

    [Fact]
    public async Task AnAncestry_AndAnAncestorsPower_AreOffered_ByTheCampaignsBuilder()
    {
        var slug = Slug();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        var saved = await worldBuilder.WorldBuild(Batch(
            new HomebrewTemplateUpsertRequest { Kind = "ancestry", System = "dnd5e", Yaml = "name: cinderfolk\nsystem: dnd5e\ntraits: [Fire Resistance]\nabilityBonuses: { Constitution: 1 }\nsize: Medium\nbaseSpeed: 30\n" },
            new HomebrewTemplateUpsertRequest { Kind = "power", System = "dnd5e", Yaml = "name: ash_mother\ntype: deity\nlabel: The Ash Mother\nclasses: [cleric]\n" }), slug);
        Assert.True(saved.Success, saved.Summary);

        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var races = (await builder.CharacterBuilder("options", new CharacterDraft(), slug, "race")).Data!.Options!;
        Assert.True(races.Single(o => o.Id == "cinderfolk").Homebrew);
        Assert.False(races.Single(o => o.Id == "human").Homebrew);

        var cleric = new CharacterDraft { Kind = "pc" }.With("race", "cinderfolk").With("class", "cleric");
        var deities = (await builder.CharacterBuilder("options", cleric, slug, "deity")).Data!.Options!;
        Assert.Equal("The Ash Mother", Assert.Single(deities).Label);

        // Another campaign sees neither.
        var elsewhere = (await builder.CharacterBuilder("options", new CharacterDraft(), Slug(), "race")).Data!.Options!;
        Assert.DoesNotContain(elsewhere, o => o.Id == "cinderfolk");
    }

    [Fact]
    public async Task ACharacterBuiltWithAHomebrewSubclass_PreviewsAndCommits_WithItsFeatures()
    {
        var slug = Slug();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        await worldBuilder.WorldBuild(Batch(new HomebrewTemplateUpsertRequest { Kind = "classOption", System = "dnd5e", Yaml = EmberKnight }), slug);
        var draft = CharacterCreationTests.Wizard()
            .With("class", "fighter")
            .With("skills", new[] { "Athletics", "Perception" });

        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var plain = await builder.CharacterBuilder("preview", draft, slug);
        Assert.True(plain.Success, plain.Summary);

        var level3 = (draft with { Level = 3 }).With("levels", new System.Collections.Generic.Dictionary<string, object> { ["1.fightingStyle"] = "defense", ["3.subclass"] = "ember_knight" });
        var preview = await builder.CharacterBuilder("preview", level3, slug);
        Assert.True(preview.Success, preview.Summary);
        Assert.True(!(preview.Data!.Errors ?? []).Any(e => e.Step == "levels"), string.Join(" ", (preview.Data.Errors ?? []).Select(e => $"[{e.Step}] {e.Message}")));
        Assert.Contains(preview.Data.ClassFeatures ?? [], f => f.Name == "Kindled Blade");
    }

    [Fact]
    public async Task ACampaignsOwnFeatsAndSpells_AreOfferedInItsBuilder_TaggedHomebrew_AndNowhereElse()
    {
        var mine = Slug();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        var saved = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Feats = [new CustomFeatUpsertRequest { Id = "feats/lantern-sense", Name = "Lantern Sense", System = "dnd5e", Description = "You see in lantern light as in day." }],
            Spells = [new CustomSpellUpsertRequest { Id = "spells/ember-ward", Name = "Ember Ward", System = "dnd5e", Level = 1, Classes = ["wizard"], Description = "A ring of cinders." }],
        }, mine);
        Assert.True(saved.Success, saved.Summary);

        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var wizard = CharacterCreationTests.Wizard();
        var fighter = (await builder.CharacterBuilder("options", Fighter(4), mine, "levels")).Data!.Slots!;
        var improvement = fighter.Single(sl => sl.Id == "4.asiOrFeat");
        var feat = improvement.Options.Single(o => o.Id == "Lantern Sense");
        Assert.True(feat.Homebrew);
        Assert.Contains(improvement.Options, o => o.Id.Equals("grappler", StringComparison.OrdinalIgnoreCase) && !o.Homebrew);

        var spells = (await builder.CharacterBuilder("options", wizard, mine, "spells")).Data!.Options!;
        Assert.True(spells.Single(o => o.Id == "Ember Ward").Homebrew);

        var elsewhere = Slug();
        var otherFighter = (await builder.CharacterBuilder("options", Fighter(4), elsewhere, "levels")).Data!.Slots!;
        Assert.DoesNotContain(otherFighter.Single(sl => sl.Id == "4.asiOrFeat").Options, o => o.Id == "Lantern Sense");
        var otherSpells = (await builder.CharacterBuilder("options", wizard, elsewhere, "spells")).Data!.Options!;
        Assert.DoesNotContain(otherSpells, o => o.Id == "Ember Ward");

        // The pick is a real pick: previewed and committed without an unknown-feat or unknown-spell error.
        var draft = (Fighter(4).With("skills", new[] { "Athletics", "Perception" }))
            .With("levels", new System.Collections.Generic.Dictionary<string, object> { ["1.fightingStyle"] = "defense", ["3.subclass"] = "champion", ["4.asiOrFeat"] = "Lantern Sense" });
        var preview = await builder.CharacterBuilder("preview", draft, mine);
        Assert.True(!(preview.Data!.Errors ?? []).Any(e => e.Step == "levels"), string.Join(" ", (preview.Data.Errors ?? []).Select(e => $"[{e.Step}] {e.Message}")));
    }
}
