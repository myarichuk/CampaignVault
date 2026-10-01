using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>character_builder against RavenDB: commit goes through world_build and is idempotent on the draft id.</summary>
[Collection("RavenDB")]
public class CharacterBuilderToolsTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    [Fact]
    public async Task Commit_MatchesWorldBuild_ForTheSameCharacter()
    {
        var slug = "builder-commit-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);

        var committed = await builder.CharacterBuilder("commit", CharacterCreationTests.Wizard(), slug);
        Assert.True(committed.Success, committed.Summary);

        // The same wizard, written by hand the way the model would.
        var stats = new Dnd5eExtension
        {
            Strength = 8, Dexterity = 13, Constitution = 14, Intelligence = 15, Wisdom = 12, Charisma = 10,
            Race = "human", Background = "sage", Level = 1, HitDie = "d6",
            ClassLevels = [new ClassLevelEntry { Class = "Wizard", Level = 1 }],
        };
        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "skills", Value = "Insight" });
        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "skills", Value = "Investigation" });
        var built = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Characters = [new CharacterUpsertRequest { Id = "chars/hand-built-wizard", Name = "Ilsa Venn", ClassLevel = "Wizard 1", IsPc = true, SystemStats = stats }],
        }, slug);
        Assert.True(built.Success, built.Summary);

        var fromBuilder = await Load(slug, committed.Data!.Character!.Id);
        var byHand = await Load(slug, "chars/hand-built-wizard");
        var a = Assert.IsType<Dnd5eExtension>(fromBuilder.SystemStats);
        var b = Assert.IsType<Dnd5eExtension>(byHand.SystemStats);

        Assert.True(fromBuilder.IsPc);
        Assert.Equal(8, fromBuilder.MaxHp);
        Assert.Equal(byHand.MaxHp, fromBuilder.MaxHp);
        Assert.Equal(byHand.CurrentHp, fromBuilder.CurrentHp);
        Assert.Equal(b.Intelligence, a.Intelligence);
        Assert.Equal(b.SpellSaveDc, a.SpellSaveDc);
        Assert.Equal(b.SkillModifiers.OrderBy(kv => kv.Key), a.SkillModifiers.OrderBy(kv => kv.Key));
        Assert.Equal(b.SavingThrowModifiers.OrderBy(kv => kv.Key), a.SavingThrowModifiers.OrderBy(kv => kv.Key));
        Assert.Equal(b.Attributes["proficiencyBonus"], a.Attributes["proficiencyBonus"]);
        Assert.Equal(["fire_bolt", "light", "mage_hand"], a.Spells.Cantrips);
    }

    [Fact]
    public async Task Commit_AgainWithTheReturnedId_UpdatesInPlace_WithoutStackingRacialBonuses()
    {
        var slug = "builder-recommit-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var first = await builder.CharacterBuilder("commit", CharacterCreationTests.Wizard(), slug);
        Assert.True(first.Success, first.Summary);
        var id = first.Data!.Character!.Id;

        var second = await builder.CharacterBuilder("commit", CharacterCreationTests.Wizard() with { Id = id, Look = "Ink to the elbows." }, slug);
        Assert.True(second.Success, second.Summary);

        var stored = await Load(slug, id);
        Assert.Equal(16, Assert.IsType<Dnd5eExtension>(stored.SystemStats).Intelligence);
        Assert.Equal("Ink to the elbows.", stored.CurrentAppearance);
        Assert.Equal(2, Assert.IsType<Dnd5eExtension>(stored.SystemStats).LevelUpChoices.Count(c => c.Key == "skills"));
    }

    [Fact]
    public async Task Commit_WithErrors_WritesNothing()
    {
        var slug = "builder-invalid-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var result = await builder.CharacterBuilder("commit", CharacterCreationTests.Wizard().With("skills", new[] { "Insight" }) with { Id = "chars/never" }, slug);

        Assert.False(result.Success);
        Assert.Contains(result.Data!.Errors, e => e.Step == "skills");
        using var session = fixture.Store.OpenAsyncSession();
        Assert.Null(await session.LoadAsync<Character>("chars/never", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Steps_And_Options_ComeFromTheCampaignsSystem()
    {
        var slug = "builder-steps-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var steps = await builder.CharacterBuilder("steps", new CharacterDraft { Kind = "pc" }, slug);
        var classes = await builder.CharacterBuilder("options", new CharacterDraft { Kind = "pc" }, slug, step: "class");
        var spells = await builder.CharacterBuilder("options", new CharacterDraft().With("class", "wizard"), slug, step: "spells");

        Assert.True(steps.Success, steps.Summary);
        Assert.Equal("dnd5e", steps.Data!.System);
        Assert.Equal("race", steps.Data.Steps![0].Key);
        Assert.Contains(classes.Data!.Options!, o => o.Id == "wizard" && o.Label == "Wizard");
        Assert.Equal(3, spells.Data!.GroupCounts![SpellGroups.Cantrips]);
        Assert.Equal(6, spells.Data.GroupCounts[SpellGroups.Known]);
        Assert.Contains(spells.Data.Options!, o => o.Id == "fire_bolt" && o.Group == SpellGroups.Cantrips);
    }

    [Fact]
    public async Task BeforeFinalize_TheBuilderPlaysTheSystemChosenInOnboarding()
    {
        var slug = "builder-onboarding-" + Guid.NewGuid().ToString("N")[..8];
        var onboarding = TestCampaignToolsFactory.CreateTool<OnboardingTools>(fixture);
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        Assert.True((await onboarding.StartCampaignOnboarding(slug)).Success);
        Assert.True((await onboarding.SubmitOnboardingAnswer(slug, slug)).Success);
        Assert.True((await onboarding.SubmitOnboardingAnswer(slug, "Pathfinder2e")).Success);

        var steps = await builder.CharacterBuilder("steps", new CharacterDraft { Kind = "pc" }, slug);

        // No pf2e recipe yet (phase 6), but it is the pf2e one that was asked for, not a silent 5e sheet.
        Assert.False(steps.Success && steps.Data!.System == "dnd5e", steps.Summary);
    }

    private async Task<Character> Load(string slug, string id)
    {
        using var session = fixture.Store.OpenAsyncSession();
        var character = await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken);
        Assert.NotNull(character);
        Assert.Equal(slug, character.CampaignName);
        return character;
    }
}
