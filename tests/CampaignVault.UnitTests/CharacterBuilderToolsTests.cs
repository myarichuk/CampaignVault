using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Tools;
using Raven.Client.Documents;
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
            Race = "human", Background = "acolyte", Level = 1, HitDie = "d6",
            ClassLevels = [new ClassLevelEntry { Class = "Wizard", Level = 1 }],
        };
        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "skills", Value = "Arcana" });
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
    public async Task Commit_GivesANewCharacterItsBackgroundsEquipmentAndGold_Once()
    {
        var slug = "builder-gear-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var committed = await builder.CharacterBuilder("commit", CharacterCreationTests.Wizard(), slug);
        Assert.True(committed.Success, committed.Summary);
        var id = committed.Data!.Character!.Id;
        var again = await builder.CharacterBuilder("commit", CharacterCreationTests.Wizard() with { Id = id }, slug);
        Assert.True(again.Success, again.Summary);

        using var session = fixture.Store.OpenAsyncSession();
        var items = await session.Query<Item>().Customize(c => c.WaitForNonStaleResults()).Where(i => i.HolderId == id).ToListAsync();
        Assert.Equal(6, items.Count);
        Assert.Contains(items, i => i is { Name: "Stick of incense", Quantity: 5 });
        Assert.Equal(15, Assert.IsType<Dnd5eExtension>((await Load(slug, id)).SystemStats).ResourcePools["gold"].Current);
    }

    [Fact]
    public async Task Commit_ALevel5Wizard_SavesWhatThePreviewShowed_AndAgainDoesntStackTheImprovement()
    {
        // Phase 8's done-when: the level-5 wizard's preview is the bootstrap's output, so the saved sheet is the same.
        var slug = "builder-level5-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var preview = await builder.CharacterBuilder("preview", CharacterCreationTests.Level5Wizard(), slug);
        Assert.True(preview.Success, preview.Summary);
        Assert.Empty(preview.Data!.Errors);
        var committed = await builder.CharacterBuilder("commit", CharacterCreationTests.Level5Wizard(), slug);
        Assert.True(committed.Success, committed.Summary);

        var shown = preview.Data.Character!;
        var stored = await Load(slug, committed.Data!.Character!.Id);
        var p = Assert.IsType<Dnd5eExtension>(shown.SystemStats);
        var s = Assert.IsType<Dnd5eExtension>(stored.SystemStats);
        Assert.Equal(32, stored.MaxHp);
        Assert.Equal(shown.MaxHp, stored.MaxHp);
        Assert.Equal(shown.ClassLevel, stored.ClassLevel);
        Assert.Equal((p.Strength, p.Dexterity, p.Constitution, p.Intelligence, p.Wisdom, p.Charisma), (s.Strength, s.Dexterity, s.Constitution, s.Intelligence, s.Wisdom, s.Charisma));
        Assert.Equal(18, s.Intelligence);
        Assert.Equal(p.SpellSaveDc, s.SpellSaveDc);
        Assert.Equal(p.Attributes["proficiencyBonus"], s.Attributes["proficiencyBonus"]);
        Assert.Equal(p.SkillModifiers.OrderBy(kv => kv.Key), s.SkillModifiers.OrderBy(kv => kv.Key));
        Assert.Equal(p.SavingThrowModifiers.OrderBy(kv => kv.Key), s.SavingThrowModifiers.OrderBy(kv => kv.Key));
        Assert.Equal(p.Spells.Prepared, s.Spells.Prepared);
        Assert.Equal(p.LevelUpChoices.Select(c => (c.Level, c.Key, c.Value)), s.LevelUpChoices.Select(c => (c.Level, c.Key, c.Value)));

        // Saving again (an edit in the builder) sends the derived sheet: the improvement isn't added a second time.
        var again = await builder.CharacterBuilder("commit", CharacterCreationTests.Level5Wizard() with { Id = stored.Id, Look = "Singed sleeves." }, slug);
        Assert.True(again.Success, again.Summary);
        var after = Assert.IsType<Dnd5eExtension>((await Load(slug, stored.Id)).SystemStats);
        Assert.Equal(18, after.Intelligence);
        Assert.Single(after.LevelUpChoices, c => c.Key == "asiOrFeat");
    }

    [Fact]
    public async Task Commit_ACompanion_IsAPartyCompanionNotAPc_WithItsStatBlockHp()
    {
        var slug = "builder-companion-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var draft = new CharacterDraft { Kind = "companion", Name = "Brann", Level = 2, PartyLevel = 2, Concept = "Served with Aric in the Copper Watch; owes him a life.", Look = "Grey muzzle, torn ear." }
            .With("statblock", new System.Collections.Generic.Dictionary<string, object> { ["statBlockHp"] = 21, ["armorClass"] = 15 });

        var committed = await builder.CharacterBuilder("commit", draft, slug);
        Assert.True(committed.Success, committed.Summary);

        var brann = await Load(slug, committed.Data!.Character!.Id);
        Assert.True(brann.IsPartyCompanion);
        Assert.False(brann.IsPc);
        Assert.Equal(21, brann.MaxHp);
        Assert.Equal(15, Assert.IsType<Dnd5eExtension>(brann.SystemStats).ArmorClass);
        // A drafted companion's shared history and look are kept, not just its numbers.
        Assert.Contains("owes him a life", brann.Notes);
        Assert.Equal("Grey muzzle, torn ear.", brann.CurrentAppearance);

        // The codex ally list and the party frames read the campaign's companions: a built one is there before the DM places it.
        using var session = fixture.Store.OpenAsyncSession();
        var posture = await CampaignPostureBuilder.BuildAsync(session, fixture.CreateRepository(), new CampaignDocumentKeys(), slug,
            isNewCampaign: false, TestContext.Current.CancellationToken);
        Assert.Contains(posture.Companions, c => c.Id == brann.Id && !c.IsPc);
        Assert.DoesNotContain(posture.Pcs, c => c.Id == brann.Id);
    }

    [Fact]
    public async Task Steps_SendTheSkillNames_ForTheCompanionsSkillsField()
    {
        var slug = "builder-skills-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var steps = await builder.CharacterBuilder("steps", new CharacterDraft { Kind = "companion" }, slug);

        Assert.True(steps.Success, steps.Summary);
        var skills = steps.Data!.StatBlocks!.Single().Fields.Single(f => f.Key == "skillModifiers");
        Assert.Equal("modifiers", skills.Type);
        Assert.Equal(18, skills.Keys!.Count);
        Assert.Contains("Sleight of Hand", skills.Keys);
        Assert.True(steps.Data.StatBlocks!.Single().Fields.Single(f => f.Key == "challengeRating").Compact, "drawn as a narrow box beside the numbers");
        var type = steps.Data.StatBlocks!.Single().Fields.Single(f => f.Key == "creatureType");
        Assert.Equal("choice", type.Type);
        Assert.Equal(14, type.Keys!.Count);
    }

    [Fact]
    public async Task Commit_ADmDraftedCompanion_WithTextFieldsAndALevelAboveTheParty_PreviewsAndSaves()
    {
        var slug = "builder-drafted-" + Guid.NewGuid().ToString("N")[..8];
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var draft = new CharacterDraft
        {
            Kind = "companion", Name = "Brann Holt", Level = 2, PartyLevel = 1,
            Concept = "Aric's sergeant in the Copper Watch; he owes Aric a life.", Look = "Grey beard, dented helm.",
        }.With("statblock", new System.Collections.Generic.Dictionary<string, object>
        {
            ["statBlockHp"] = 16, ["armorClass"] = 16, ["movement"] = 30, ["challengeRating"] = "1/2",
            ["skillModifiers"] = new System.Collections.Generic.Dictionary<string, object> { ["athletics"] = 5, ["Perception"] = 2 },
            ["attacks"] = "Spear +3, 1d6+1 piercing", ["stance"] = "Loyal to Aric, wary of the mastiff",
        });

        var preview = await builder.CharacterBuilder("preview", draft, slug);
        Assert.True(preview.Success, preview.Summary);
        Assert.Empty(preview.Data!.Errors);

        var committed = await builder.CharacterBuilder("commit", draft, slug);
        Assert.True(committed.Success, committed.Summary);
        var brann = await Load(slug, committed.Data!.Character!.Id);
        Assert.Contains("Loyal to Aric", brann.Notes);
        Assert.Contains("Challenge rating: 1/2", brann.Notes);
        // Saved and loaded back: the skills given, in the table's spelling, and no others derived on top.
        var skills = Assert.IsType<Dnd5eExtension>(brann.SystemStats).SkillModifiers;
        Assert.Equal(5, skills["Athletics"]);
        Assert.Equal(2, skills["Perception"]);
        Assert.Equal(2, skills.Count);
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
    public async Task Commit_APf2eFighter_DerivesItsHpAndSkills_AndAgainKeepsItsModifiers()
    {
        var slug = await Pf2eCampaign("builder-pf2e-");

        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var first = await builder.CharacterBuilder("commit", CharacterCreationTests.Pf2eFighter(), slug);
        Assert.True(first.Success, first.Summary);
        var id = first.Data!.Character!.Id;

        var stored = await Load(slug, id);
        var stats = Assert.IsType<Pf2eExtension>(stored.SystemStats);
        Assert.Equal(22, stored.MaxHp);
        Assert.Equal((4, 2, -1), (stats.StrengthMod, stats.ConstitutionMod, stats.CharismaMod));
        Assert.Equal(Pf2eProficiencyRank.Trained, stats.SkillProficiencies["Athletics"]);

        // Committing the same id again: the dwarf's boosts and flaw aren't applied twice, nor lost.
        var second = await builder.CharacterBuilder("commit", CharacterCreationTests.Pf2eFighter() with { Id = id, Look = "Braided beard." }, slug);
        Assert.True(second.Success, second.Summary);
        var again = await Load(slug, id);
        var after = Assert.IsType<Pf2eExtension>(again.SystemStats);
        Assert.Equal((4, 2, 2, 0, 2, -1), (after.StrengthMod, after.DexterityMod, after.ConstitutionMod, after.IntelligenceMod, after.WisdomMod, after.CharismaMod));
        Assert.Equal(22, again.MaxHp);
        Assert.Equal("Braided beard.", again.CurrentAppearance);
    }

    [Fact]
    public async Task Commit_ANarrativePc_HasNoStats_AndItsNatureIsItsPsychology_AndAgainKeepsItsMemories()
    {
        var slug = await Pf2eCampaign("builder-narrative-", RulesetSystem.Narrative);
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);

        var first = await builder.CharacterBuilder("commit", CharacterCreationTests.NarrativePc(), slug);
        Assert.True(first.Success, first.Summary);
        var id = first.Data!.Character!.Id;

        var stored = await Load(slug, id);
        Assert.True(stored.IsPc);
        Assert.Equal(["Wry", "restless", "loyal to a fault"], stored.Psychology.Traits);
        Assert.Equal(["Find her brother"], stored.Psychology.Wants);
        Assert.Equal(["Deep water", "being forgotten"], stored.Psychology.Fears);
        Assert.Null(stored.ClassLevel);
        Assert.Equal(0, stored.MaxHp);

        // Play adds a memory; building her again replaces the nature, not the rest of who she has become.
        using (var session = fixture.Store.OpenAsyncSession())
        {
            var c = await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken);
            c!.Psychology.Memories["the ferry"] = new MemoryNode { Topic = "the ferry", Details = "Where her brother was last seen." };
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var draft = CharacterCreationTests.NarrativePc() with { Id = id };
        var second = await builder.CharacterBuilder("commit", draft.With("identity", new { descriptors = "Wry, restless, brave", drives = "Find her brother" }), slug);
        Assert.True(second.Success, second.Summary);
        var again = await Load(slug, id);
        Assert.Equal(["Wry", "restless", "brave"], again.Psychology.Traits);
        Assert.Empty(again.Psychology.Fears);
        Assert.True(again.Psychology.Memories.ContainsKey("the ferry"));
    }

    private async Task<string> Pf2eCampaign(string prefix, string system = RulesetSystem.Pathfinder2e)
    {
        var slug = prefix + Guid.NewGuid().ToString("N")[..8];
        var keys = new CampaignDocumentKeys();
        using var session = fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = keys.Config(slug), ActiveSystem = system }, keys.Config(slug), TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return slug;
    }

    [Fact]
    public async Task Commit_APf2eWizard_WithItsSpellbook()
    {
        var slug = await Pf2eCampaign("builder-pf2e-wizard-");
        var builder = TestCampaignToolsFactory.CreateTool<CharacterBuilderTools>(fixture);
        var draft = CharacterCreationTests.Pf2eWizardWithoutSpells();
        var spells = await builder.CharacterBuilder("options", draft, slug, step: "spells");
        Assert.True(spells.Success, spells.Summary);
        Assert.Equal(10, spells.Data!.GroupCounts![SpellGroups.Cantrips]);
        Assert.Equal(5, spells.Data.GroupCounts[SpellGroups.Known]);
        draft = draft.With("spells", new SpellChoice
        {
            Cantrips = [.. spells.Data.Options!.Where(o => o.Group == SpellGroups.Cantrips).Take(10).Select(o => o.Id)],
            Known = [.. spells.Data.Options!.Where(o => o.Group == SpellGroups.Known).Take(5).Select(o => o.Id)],
        });

        var committed = await builder.CharacterBuilder("commit", draft, slug);
        Assert.True(committed.Success, committed.Summary);

        var stored = await Load(slug, committed.Data!.Character!.Id);
        var stats = Assert.IsType<Pf2eExtension>(stored.SystemStats);
        Assert.Equal(13, stored.MaxHp); // elf 6 + wizard 6 + Con 1
        Assert.Equal(4, stats.IntelligenceMod);
        Assert.Equal(10, stats.Spells.Cantrips.Count);
        Assert.Equal(5, stats.Spells.Known.Count);
        Assert.Equal("Wizard 1", stored.ClassLevel);
        Assert.Contains("student_of_the_canon", stats.SkillFeats);
    }

    [Fact]
    public async Task WorldBuild_APf2eCharacter_KeepsAHandSetHp_AndWithoutOneGetsItsAncestryAndClassHp()
    {
        // world_build replaces a character whole. Sent with its HP, a PF2e character keeps it (the ancestry and class
        // HP inputs are filled, but a given max HP wins); sent without, it gets ancestry + class HP (it used to get 0).
        var slug = await Pf2eCampaign("worldbuild-pf2e-hp-");
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(fixture);
        CharacterUpsertRequest Dwarf(string notes, int maxHp, int currentHp) => new()
        {
            Id = "chars/hand-dwarf", Name = "Durga", ClassLevel = "Fighter 1", IsPc = true, Notes = notes, MaxHp = maxHp, CurrentHp = currentHp,
            SystemStats = new Pf2eExtension { Ancestry = "dwarf", Background = "farmhand", Level = 1, StrengthMod = 4 },
        };

        Assert.True((await worldBuilder.WorldBuild(new WorldBuildBatch { Characters = [Dwarf("First.", 30, 10)] }, slug)).Success);
        Assert.True((await worldBuilder.WorldBuild(new WorldBuildBatch { Characters = [Dwarf("Second.", 30, 10)] }, slug)).Success);
        var kept = await Load(slug, "chars/hand-dwarf");
        Assert.Equal((30, 10), (kept.MaxHp, kept.CurrentHp));
        Assert.Equal(10, Assert.IsType<Pf2eExtension>(kept.SystemStats).AncestryHp);

        Assert.True((await worldBuilder.WorldBuild(new WorldBuildBatch { Characters = [Dwarf("Third.", 0, 0)] }, slug)).Success);
        var derived = await Load(slug, "chars/hand-dwarf");
        Assert.Equal(20, derived.MaxHp); // dwarf 10 + fighter 10 + Con 0
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
