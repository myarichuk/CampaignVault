using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Phase 1 of the character builder: recipe steps, options, validators, preview, and the startup recipe check.</summary>
[Collection("PluginStatics")]
public class CharacterCreationTests : IDisposable
{
    private static readonly System.Reflection.Assembly Asm = typeof(RaceDefinitionProvider).Assembly;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv-creation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Steps_For5ePc_FollowTheRecipe_AndHideSpellsForANonCaster()
    {
        var service = Service();
        var draft = new CharacterDraft { Kind = "pc" };

        Assert.Equal(["race", "class", "background", "abilities", "skills", "spells", "identity"], Keys(service.Steps(RulesetSystem.Dnd5e, draft)));
        Assert.DoesNotContain("spells", Keys(service.Steps(RulesetSystem.Dnd5e, draft.With("class", "fighter"))));
        // A paladin casts from level 2, so a level-1 paladin has no spells step either.
        Assert.DoesNotContain("spells", Keys(service.Steps(RulesetSystem.Dnd5e, draft.With("class", "paladin"))));
        Assert.Contains("spells", Keys(service.Steps(RulesetSystem.Dnd5e, draft.With("class", "wizard"))));
    }

    [Theory]
    [InlineData("pc")]
    [InlineData("companion")]
    public void Steps_ForNarrative_AreJustIdentity_WithItsNature(string kind)
    {
        var service = Service();
        var steps = service.Steps(RulesetSystem.Narrative, new CharacterDraft { Kind = kind });

        var step = Assert.Single(steps);
        Assert.Equal(CreationStepKinds.Identity, step.Kind);
        Assert.Equal("nature", step.Schema);
        var nature = service.StatBlock(RulesetSystem.Narrative, "nature")!;
        Assert.Equal("Nature", nature.Title);
        Assert.Equal(["descriptors", "drives", "fears"], nature.Fields.Select(f => f.Key));
        Assert.All(nature.Fields, f => Assert.Equal("list", f.Type));
    }

    /// <summary>A Narrative player character: name, concept, look and nature, nothing else.</summary>
    public static CharacterDraft NarrativePc() =>
        new CharacterDraft { Kind = "pc", Name = "Wren Hollis", Concept = "A ferry pilot looking for her brother.", Look = "Tar-black hands, a coat two sizes too big." }
            .With("identity", new { descriptors = "Wry, restless, loyal to a fault", drives = "Find her brother", fears = "Deep water; being forgotten." });

    [Fact]
    public async Task Narrative_Preview_HasNoStats_AndItsNatureIsItsPsychology()
    {
        var preview = await Service().PreviewAsync(RulesetSystem.Narrative, NarrativePc());

        Assert.Empty(preview.Errors);
        Assert.Empty(preview.Warnings);
        var c = preview.Character;
        Assert.True(c.IsPc);
        Assert.Equal("A ferry pilot looking for her brother.", c.Notes);
        Assert.Equal("Tar-black hands, a coat two sizes too big.", c.CurrentAppearance);
        Assert.Equal(["Wry", "restless", "loyal to a fault"], c.Psychology.Traits);
        Assert.Equal(["Find her brother"], c.Psychology.Wants);
        Assert.Equal(["Deep water", "being forgotten"], c.Psychology.Fears);
        Assert.Equal(typeof(SystemExtension), c.SystemStats!.GetType());
        Assert.Null(c.ClassLevel);
        Assert.Equal(0, c.MaxHp);
    }

    [Theory]
    [InlineData("Wry, restless", "Three descriptors: three, separated by commas (you gave 2).")]
    [InlineData("Wry, restless, loyal, tired", "Three descriptors: three, separated by commas (you gave 4).")]
    [InlineData("", null)]
    [InlineData("Wry; restless\nloyal", null)]
    public void Narrative_Descriptors_AreThree_OrNone(string descriptors, string? error)
    {
        var draft = new CharacterDraft { Kind = "pc", Name = "Wren" }.With("identity", new { descriptors });

        var errors = Service().Validate(RulesetSystem.Narrative, draft).Where(i => !i.IsWarning).Select(i => i.Message).ToList();

        if (error is null)
            Assert.Empty(errors);
        else
            Assert.Equal(error, Assert.Single(errors));
    }

    [Fact]
    public void Narrative_Nature_AsLists_IsTheSameAsText_AndTooManyDrivesIsAnError()
    {
        var draft = new CharacterDraft { Kind = "pc", Name = "Wren" }
            .With("identity", new { descriptors = new[] { "Wry", "restless", "loyal" }, drives = new[] { "a", "b", "c", "d", "e", "f" } });

        var error = Assert.Single(Service().Validate(RulesetSystem.Narrative, draft).Where(i => !i.IsWarning));

        Assert.Equal("Drives: up to five, separated by commas (you gave 6).", error.Message);
    }

    [Fact]
    public void Options_ForSkills_AreTheClassList_WithoutTheBackgroundsSkills_AndCountComesFromTheClass()
    {
        var service = Service();
        var draft = new CharacterDraft().With("class", "wizard").With("background", "acolyte");

        var options = service.Options(RulesetSystem.Dnd5e, "skills", draft).Select(o => o.Id).ToList();
        var skillsStep = service.Steps(RulesetSystem.Dnd5e, draft).Single(s => s.Key == "skills");

        Assert.Equal(["Arcana", "History", "Investigation", "Medicine"], options);
        Assert.Equal(2, service.Context(RulesetSystem.Dnd5e, draft).Count(skillsStep, null));
    }

    [Fact]
    public void Validate_ACompleteWizard_HasNoErrors()
    {
        var issues = Service().Validate(RulesetSystem.Dnd5e, Wizard());

        Assert.Empty(issues.Where(i => !i.IsWarning));
    }

    [Fact]
    public void Validate_PointBuyOverBudget_IsAnError()
    {
        var draft = Wizard().With("abilities", new AbilityScoreChoice
        {
            Method = "pointBuy",
            Scores = new() { ["Strength"] = 15, ["Dexterity"] = 15, ["Constitution"] = 15, ["Intelligence"] = 15, ["Wisdom"] = 8, ["Charisma"] = 8 },
        });

        var issues = Service().Validate(RulesetSystem.Dnd5e, draft);

        Assert.Contains(issues, i => i.Step == "abilities" && !i.IsWarning && i.Message.Contains("36 points spent, budget is 27"));
    }

    [Fact]
    public void Validate_StandardArrayWithAWrongValue_IsAnError()
    {
        var draft = Wizard().With("abilities", new AbilityScoreChoice
        {
            Method = "standardArray",
            Scores = new() { ["Strength"] = 15, ["Dexterity"] = 15, ["Constitution"] = 13, ["Intelligence"] = 12, ["Wisdom"] = 10, ["Charisma"] = 8 },
        });

        Assert.Contains(Service().Validate(RulesetSystem.Dnd5e, draft), i => i.Step == "abilities" && i.Message.Contains("Standard array"));
    }

    [Fact]
    public void Validate_TooManySkills_OrOneTheBackgroundAlreadyGives_IsAnError()
    {
        var service = Service();

        var tooMany = service.Validate(RulesetSystem.Dnd5e, Wizard().With("skills", new[] { "Arcana", "Investigation", "Medicine" }));
        var excluded = service.Validate(RulesetSystem.Dnd5e, Wizard().With("skills", new[] { "Arcana", "Religion" }));

        Assert.Contains(tooMany, i => i.Step == "skills" && i.Message.Contains("Too many: pick 2, not 3"));
        Assert.Contains(excluded, i => i.Step == "skills" && i.Message.Contains("Religion"));
    }

    [Fact]
    public void Validate_SpellCountsFollowTheProgression()
    {
        var service = Service();
        var wrong = Wizard().With("spells", new SpellChoice
        {
            Cantrips = ["fire_bolt", "light"],
            Known = ["magic_missile", "shield", "sleep", "burning_hands", "detect_magic", "mage_armor"],
            Prepared = ["magic_missile", "shield", "sleep", "burning_hands", "detect_magic"],
        });

        var issues = service.Validate(RulesetSystem.Dnd5e, wrong);

        Assert.Contains(issues, i => i.Step == "spells" && i.Message.Contains("Pick 3 cantrips"));
        // Int 16 (+3) at level 1 prepares 4.
        Assert.Contains(issues, i => i.Step == "spells" && i.Message.Contains("Too many prepared: 4 a day"));
    }

    [Fact]
    public void Validate_MissingChoices_AreErrorsPerStep()
    {
        var issues = Service().Validate(RulesetSystem.Dnd5e, new CharacterDraft { Kind = "pc" });

        Assert.Equal(["race", "class", "background", "abilities", "skills", "spells", "identity"], issues.Select(i => i.Step).Distinct());
    }

    [Fact]
    public async Task Preview_Level1Wizard_DerivesHpProficiencySkillsAndSpellDc()
    {
        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, Wizard());

        Assert.Empty(preview.Errors);
        var stats = Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats);
        // Human: +1 to every score, from the race step only.
        Assert.Equal(16, stats.Intelligence);
        Assert.Equal(15, stats.Constitution);
        Assert.Equal(6 + 2, preview.Character.MaxHp);
        Assert.Equal(2, stats.Attributes["proficiencyBonus"]);
        Assert.Equal(8 + 2 + 3, stats.SpellSaveDc);
        Assert.Equal("Wizard 1", preview.Character.ClassLevel);
        Assert.Equal(3 + 2, stats.SkillModifiers["Investigation"]); // chosen class skill
        Assert.Equal(3 + 2, stats.SkillModifiers["Religion"]);      // background (acolyte)
        Assert.Equal(3 + 2, stats.SavingThrowModifiers["Intelligence"]);
        Assert.Equal(["fire_bolt", "light", "mage_hand"], stats.Spells.Cantrips);
        Assert.Equal(6, stats.Spells.Known.Count);
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 1, Key: "skills", Value: "Arcana" });
        Assert.True(preview.Character.IsPc);
    }

    [Fact]
    public void Patch_FromAPluginRoot_AddsAStep_WhereItsAfterSaysSo()
    {
        WritePluginRecipe("deity_patch.yaml", """
            patches: pc
            steps+:
              - { key: oath, kind: pickOne, after: background, prompt: Oath }
            """);

        var steps = Service(pluginRoot: Path.Combine(_root, "plugin")).Steps(RulesetSystem.Dnd5e, new CharacterDraft());

        Assert.Equal(["race", "class", "background", "oath", "abilities", "skills", "spells", "identity"], Keys(steps));
    }

    private void WritePluginFile(string folder, string file, string yaml)
    {
        var dir = Path.Combine(_root, "plugin", "dnd5e", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), yaml);
    }

    private CharacterCreationService ServiceWithDeities()
    {
        // The recipe's own deity step (core) lists them; no patch is needed.
        foreach (var domain in new[] { "light", "war", "trickery" })
            WritePluginFile("classOptions", $"{domain}.yaml", $"name: {domain}\nclass: cleric\nlabel: {domain} Domain\n");
        WritePluginFile("powers", "lantern_keeper.yaml", """
            name: lantern_keeper
            type: deity
            label: The Lantern Keeper
            classes: [cleric]
            offers: [light, trickery]
            """);
        WritePluginFile("powers", "wandering_patron.yaml", "name: wandering_patron\ntype: patron\nclasses: [warlock]\noffers: [fiend]\n");
        return Service(pluginRoot: Path.Combine(_root, "plugin"));
    }

    [Fact]
    public void NamedPowers_AreListedByTypeAndClass_AndNarrowTheClassChoice()
    {
        var service = ServiceWithDeities();
        var cleric = new CharacterDraft { Kind = "pc", Level = 1 }.With("race", "human").With("class", "cleric");

        // The deity step lists gods for a cleric; the warlock's patron isn't one, and nothing ships.
        var deities = service.Options(RulesetSystem.Dnd5e, "deity", cleric);
        Assert.Equal("lantern_keeper", Assert.Single(deities).Id);
        Assert.True(deities[0].Homebrew);
        Assert.Equal("The Lantern Keeper", deities[0].Label);
        Assert.Empty(service.Options(RulesetSystem.Dnd5e, "deity", cleric.With("class", "fighter")));

        // Without a deity the cleric chooses among every domain; with one, only the domains it offers.
        Assert.Contains(service.LevelSlots(RulesetSystem.Dnd5e, cleric).Single(s => s.Key == "subclass").Options, o => o.Id == "war");
        var narrowed = service.LevelSlots(RulesetSystem.Dnd5e, cleric.With("deity", "lantern_keeper")).Single(s => s.Key == "subclass");
        Assert.Equal(["light", "trickery"], narrowed.Options.Select(o => o.Id).Order().ToArray());

        string[] Errors(string domain) =>
            [.. service.Validate(RulesetSystem.Dnd5e, cleric.With("deity", "lantern_keeper").With("levels", new Dictionary<string, object> { ["1.subclass"] = domain }))
                .Where(i => !i.IsWarning && i.Step == "levels").Select(i => i.Message)];
        Assert.Empty(Errors("light"));
        Assert.NotEmpty(Errors("war"));
    }

    [Fact]
    public async Task NamedPower_PickedInTheBuilder_IsRecordedOnTheCharacter()
    {
        var service = ServiceWithDeities();
        WritePluginFile("powers", "hearth.yaml", "name: hearth\ntype: deity\n");
        service = Service(pluginRoot: Path.Combine(_root, "plugin"));
        var draft = Wizard().With("deity", "hearth");

        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, draft);

        var stats = Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats);
        Assert.Contains(stats.LevelUpChoices, c => c is { Key: "deity", Value: "hearth" });
    }

    [Fact]
    public void Pf2eCleric_PicksDomainsAndFontFreely_WithoutADeity_AndFromTheDeitysWithOne()
    {
        var dir = Path.Combine(_root, "plugin", "pf2e");
        Directory.CreateDirectory(Path.Combine(dir, "powers"));
        File.WriteAllText(Path.Combine(dir, "powers", "lantern_keeper.yaml"), """
            name: lantern_keeper
            type: deity
            classes: [cleric]
            narrows: { domain: [healing, sun, truth], font: [healingFont] }
            """);
        var service = Service(pluginRoot: Path.Combine(_root, "plugin"));
        var cleric = new CharacterDraft { Kind = "pc", Level = 1 }.With("class", "cleric");

        var free = service.LevelSlots(RulesetSystem.Pathfinder2e, cleric);
        var domains = free.Single(s => s.Key == "domain");
        Assert.Equal(2, domains.Picks);
        Assert.True(domains.Options.Count > 20);
        Assert.Equal(["harmfulFont", "healingFont"], free.Single(s => s.Key == "font").Options.Select(o => o.Id).Order().ToArray());

        var withDeity = service.LevelSlots(RulesetSystem.Pathfinder2e, cleric.With("deity", "lantern_keeper"));
        Assert.Equal(["healing", "sun", "truth"], withDeity.Single(s => s.Key == "domain").Options.Select(o => o.Id).Order().ToArray());
        Assert.Equal("healingFont", Assert.Single(withDeity.Single(s => s.Key == "font").Options).Id);
    }

    [Fact]
    public void NamedPower_OfferingNothingTheClassHas_LeavesTheChoiceWhole()
    {
        var service = ServiceWithDeities();
        WritePluginFile("powers", "stray.yaml", "name: stray\ntype: deity\noffers: [no_such_domain]\n");
        var cleric = new CharacterDraft { Kind = "pc", Level = 1 }.With("race", "human").With("class", "cleric").With("deity", "stray");

        var slot = ServiceWithDeities().LevelSlots(RulesetSystem.Dnd5e, cleric).Single(s => s.Key == "subclass");

        Assert.Contains(slot.Options, o => o.Id == "war");
        Assert.Contains(slot.Options, o => o.Id == "light");
    }

    [Fact]
    public void ValidateRecipes_ThePowerSources_AreKnown() => ServiceWithDeities().ValidateRecipes();

    [Fact]
    public void ValidateRecipes_WithAnUnknownValidatorName_FailsWithAClearError()
    {
        WritePluginRecipe("bad_patch.yaml", """
            patches: pc
            steps+:
              - { key: oath, kind: pickN, count: 1, validators: [oath.mustBeSworn] }
            """);

        var service = Service(pluginRoot: Path.Combine(_root, "plugin"));

        var ex = Assert.Throws<InvalidOperationException>(service.ValidateRecipes);
        Assert.Contains("dnd5e recipe 'pc', step 'oath': no validator named 'oath.mustBeSworn'", ex.Message);
    }

    [Fact]
    public void ValidateRecipes_AStatBlockFieldWithNoWhereToLand_FailsAtStartup()
    {
        var dir = Path.Combine(_root, "plugin", "dnd5e", "statblocks");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "companion.yaml"), """
            name: companion
            system: dnd5e
            fields:
              - { key: armorClass, label: Armor class, type: int }
              - { key: wingspan, label: Wingspan, type: int }
            """);

        var ex = Assert.Throws<InvalidOperationException>(Service(pluginRoot: Path.Combine(_root, "plugin")).ValidateRecipes);

        Assert.Contains("stat block 'companion': field 'wingspan' has no stats field", ex.Message);
        Assert.DoesNotContain("'armorClass'", ex.Message);
    }

    [Fact]
    public void ValidateRecipes_TheShippedRecipesAreValid() => Service().ValidateRecipes();

    [Fact]
    public void ProficiencyStep_DerivesRecordedSkillChoices_AndDropsTheHint()
    {
        var stats = new Dnd5eExtension { Intelligence = 16, Wisdom = 12, Level = 1, HitDie = "d6" };
        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "skills", Value = "insight" });
        var character = new Character { Id = "chars/x", Name = "Ilsa", ClassLevel = "Wizard 1", SystemStats = stats };
        var step = new Dnd5eDeriveProficiencyStep();

        var result = step.ApplyAsync(new BootstrapContext { Character = character, ActiveSystem = RulesetSystem.Dnd5e }).Result;

        Assert.Equal(1 + 2, stats.SkillModifiers["Insight"]);
        Assert.Empty(result!.LlmHints);
    }

    private void WritePluginRecipe(string file, string yaml)
    {
        var dir = Path.Combine(_root, "plugin", "dnd5e", "creation");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), yaml);
    }

    /// <summary>
    /// The Phase 8 wizard: level 5, School of Evocation at 2, +2 Intelligence at 4, and spells through level 3 (4
    /// cantrips, 14 in the spellbook, Intelligence modifier + 5 prepared).
    /// </summary>
    internal static CharacterDraft Level5Wizard() =>
        (Wizard() with { Level = 5 })
            .With("levels", new Dictionary<string, object> { ["2.subclass"] = "evocation", ["4.asiOrFeat"] = new[] { "Intelligence" } })
            .With("spells", new SpellChoice
            {
                Cantrips = ["fire_bolt", "light", "mage_hand", "prestidigitation"],
                Known =
                [
                    "magic_missile", "shield", "sleep", "burning_hands", "detect_magic", "mage_armor", "find_familiar", "identify",
                    "misty_step", "scorching_ray", "mirror_image", "hold_person", "fireball", "counterspell",
                ],
                Prepared = ["magic_missile", "shield", "mage_armor", "detect_magic", "misty_step", "scorching_ray", "mirror_image", "fireball", "counterspell"],
            });

    [Fact]
    public void Steps_ALevel5Wizard_AsksForItsTraditionAndImprovement_ALevel1WizardForNeither()
    {
        var service = Service();

        Assert.Equal(["race", "class", "background", "abilities", "skills", "levels", "spells", "identity"], Keys(service.Steps(RulesetSystem.Dnd5e, Level5Wizard())));
        Assert.DoesNotContain("levels", Keys(service.Steps(RulesetSystem.Dnd5e, Wizard())));
        // Before a class is chosen, a draft above level 1 shows the step; at level 1 it doesn't.
        Assert.Contains("levels", Keys(service.Steps(RulesetSystem.Dnd5e, new CharacterDraft { Level = 3 })));
        Assert.DoesNotContain("levels", Keys(service.Steps(RulesetSystem.Dnd5e, new CharacterDraft())));

        var slots = service.LevelSlots(RulesetSystem.Dnd5e, Level5Wizard());
        Assert.Equal(["2.subclass", "4.asiOrFeat"], slots.Select(s => s.Id));
        Assert.Equal("Level 2 · Arcane Tradition", slots[0].Title);
        Assert.Equal("Level 4 · Ability Score Improvement", slots[1].Title);
        Assert.Equal(2, slots[1].Picks);
        Assert.Equal(CreationSources.AbilityNames, slots[1].Abilities);

        var options = service.Options(RulesetSystem.Dnd5e, "levels", Level5Wizard());
        Assert.Contains(options, o => o is { Id: "evocation", Label: "School of Evocation", Group: "2.subclass" });
        Assert.Contains(options, o => o is { Id: "Intelligence", Group: "4.asiOrFeat" });
        // A feat instead of the improvement (the SRD's one feat), its prerequisite shown.
        Assert.Contains(options, o => o.Id == "grappler" && o.Group == "4.asiOrFeat" && o.Description!.Contains("Strength 13"));
    }

    [Fact]
    public async Task Preview_Level5Wizard_HasItsTraditionImprovementAndSpells_AndAverageHp()
    {
        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, Level5Wizard());

        Assert.Empty(preview.Errors);
        Assert.Empty(preview.Warnings);
        var stats = Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats);
        Assert.Equal("Wizard 5", preview.Character.ClassLevel);
        Assert.Equal(15 + 1 + 2, stats.Intelligence); // array, human, improvement
        // d6 at level 1, then the average (4) for each of the other four, Constitution +2 at every level.
        Assert.Equal(6 + 2 + 4 * (4 + 2), preview.Character.MaxHp);
        Assert.Equal(3, stats.Attributes["proficiencyBonus"]);
        Assert.Equal(8 + 3 + 4, stats.SpellSaveDc);
        Assert.Equal(4, stats.Spells.Cantrips.Count);
        Assert.Equal(14, stats.Spells.Known.Count);
        Assert.Equal(9, stats.Spells.Prepared.Count);
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 2, Key: "subclass", Value: "evocation" });
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 4, Key: "asiOrFeat", Value: "Intelligence +2" });
        Assert.Contains(preview.Notes, n => n.Contains("doesn't roll"));
    }

    [Fact]
    public void Validate_Level5Wizard_FlagsEachWrongOrMissingLevelChoice_OnTheLevelsStep()
    {
        var service = Service();
        string[] Errors(CharacterDraft draft) => [.. service.Validate(RulesetSystem.Dnd5e, draft).Where(i => !i.IsWarning && i.Step == "levels").Select(i => i.Message)];

        var missing = Level5Wizard().With("levels", new Dictionary<string, object> { ["4.asiOrFeat"] = new[] { "Intelligence" } });
        Assert.Equal(["Level 2 · Arcane Tradition: choose one."], Errors(missing));

        // A model's made-up school stays flagged; it's never swapped for a real one.
        var madeUp = Level5Wizard().With("levels", new Dictionary<string, object> { ["2.subclass"] = "pyromancy", ["4.asiOrFeat"] = "Intelligence" });
        Assert.Equal(["Level 2 · Arcane Tradition: 'pyromancy' isn't one of the options."], Errors(madeUp));

        var three = Level5Wizard().With("levels", new Dictionary<string, object> { ["2.subclass"] = "evocation", ["4.asiOrFeat"] = new[] { "Intelligence", "Wisdom", "Charisma" } });
        Assert.Contains("raise one ability by 2 or two by 1", Assert.Single(Errors(three)));

        var featAndAbility = Level5Wizard().With("levels", new Dictionary<string, object> { ["2.subclass"] = "evocation", ["4.asiOrFeat"] = new[] { "grappler", "Intelligence" } });
        Assert.Contains(Errors(featAndAbility), e => e.Contains("one feat, or raise one or two abilities, not both"));

        // Grappler needs Strength 13; the wizard has 8 (+1 human).
        var grappler = Level5Wizard().With("levels", new Dictionary<string, object> { ["2.subclass"] = "evocation", ["4.asiOrFeat"] = "grappler" });
        Assert.Equal(["Level 4 · Ability Score Improvement: Grappler needs Strength 13."], Errors(grappler));

        // 20 is the cap: a rolled 18, +1 human, +2 improvement is 21.
        var over = Level5Wizard().With("abilities", new AbilityScoreChoice
        {
            Method = "roll",
            Scores = new() { ["Strength"] = 8, ["Dexterity"] = 13, ["Constitution"] = 14, ["Intelligence"] = 18, ["Wisdom"] = 12, ["Charisma"] = 10 },
        });
        Assert.Equal(["Intelligence would be 21; ability score improvements stop at 20."], Errors(over));
    }

    [Fact]
    public void Validate_APickForALevelTheDraftHasntReached_IsIgnoredWithAWarning()
    {
        var draft = Level5Wizard() with { Level = 3 };

        var issues = Service().Validate(RulesetSystem.Dnd5e, draft.With("spells", new SpellChoice
        {
            Cantrips = ["fire_bolt", "light", "mage_hand"],
            Known = ["magic_missile", "shield", "sleep", "burning_hands", "detect_magic", "mage_armor", "misty_step", "scorching_ray", "mirror_image", "hold_person"],
            Prepared = ["magic_missile", "shield", "mage_armor", "detect_magic", "misty_step", "scorching_ray"],
        }));

        Assert.DoesNotContain(issues, i => !i.IsWarning);
        Assert.Contains(issues, i => i is { IsWarning: true, Step: "levels" } && i.Message.Contains("'4.asiOrFeat'"));
    }

    [Fact]
    public async Task LevelChoices_AFighterChoosesItsFightingStyleAtLevel1_AndAFeatInsteadOfAnImprovement()
    {
        var service = Service();
        var fighter = new CharacterDraft { Kind = "pc", Name = "Brakka", Level = 1 }
            .With("race", "human").With("class", "fighter").With("background", "acolyte")
            .With("abilities", new AbilityScoreChoice
            {
                Method = "standardArray",
                Scores = new() { ["Strength"] = 15, ["Dexterity"] = 13, ["Constitution"] = 14, ["Intelligence"] = 8, ["Wisdom"] = 12, ["Charisma"] = 10 },
            })
            .With("skills", new[] { "Perception", "Survival" });

        Assert.Contains("levels", Keys(service.Steps(RulesetSystem.Dnd5e, fighter)));
        Assert.Contains(service.Validate(RulesetSystem.Dnd5e, fighter), i => i.Message == "Level 1 · Fighting Style: choose one.");

        var level4 = (fighter with { Level = 4 }).With("levels", new Dictionary<string, object>
        {
            ["1.fightingStyle"] = "defense",
            ["3.subclass"] = "champion",
            ["4.asiOrFeat"] = "grappler",
        });
        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, level4);

        Assert.Empty(preview.Errors);
        var stats = Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats);
        Assert.Contains("grappler", stats.Feats);
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 1, Key: "fightingStyle", Value: "defense" });
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 4, Key: "asiOrFeat", Value: "grappler" });
        Assert.Equal(15 + 1, stats.Strength); // a feat, so no improvement
    }

    private static CharacterDraft Dnd5e(string cls, int level, int str = 10, int dex = 14, int con = 13, int intel = 8, int wis = 12, int cha = 15) =>
        new CharacterDraft { Kind = "pc", Name = "Test", Level = level }
            .With("race", "human").With("class", cls).With("background", "acolyte")
            .With("abilities", new AbilityScoreChoice
            {
                Method = "pointBuy",
                Scores = new() { ["Strength"] = str, ["Dexterity"] = dex, ["Constitution"] = con, ["Intelligence"] = intel, ["Wisdom"] = wis, ["Charisma"] = cha },
            });

    [Fact]
    public void SubclassChoices_AppearOncePicked_AtTheirLevels()
    {
        var service = Service();
        var ranger = Dnd5e("ranger", 7);

        Assert.DoesNotContain(service.LevelSlots(RulesetSystem.Dnd5e, ranger), s => s.Key == "huntersPrey");

        var hunter = ranger.With("levels", new Dictionary<string, object> { ["3.subclass"] = "hunter" });
        var slots = service.LevelSlots(RulesetSystem.Dnd5e, hunter);
        var prey = slots.Single(s => s.Id == "3.huntersPrey");
        Assert.Equal("Level 3 · Hunter's Prey", prey.Title);
        Assert.Equal(["colossusSlayer", "giantKiller", "hordeBreaker"], prey.Options.Select(o => o.Id));
        Assert.Contains(slots, s => s.Id == "7.defensiveTactics");
        Assert.DoesNotContain(slots, s => s.Id == "11.multiattack");
        Assert.Contains(service.Validate(RulesetSystem.Dnd5e, hunter), i => i.Message == "Level 7 · Defensive Tactics: choose one.");
    }

    [Fact]
    public void SubclassChoices_AChampionsSecondFightingStyle_OffersTheList_ButNotTheOneItHas()
    {
        var service = Service();
        var champion = Dnd5e("fighter", 10, str: 15, dex: 13, con: 14, cha: 8).With("skills", new[] { "Perception", "Survival" })
            .With("levels", new Dictionary<string, object>
            {
                ["1.fightingStyle"] = "defense", ["3.subclass"] = "champion", ["4.asiOrFeat"] = "Strength",
                ["6.asiOrFeat"] = "Constitution", ["8.asiOrFeat"] = "Dexterity", ["10.fightingStyle"] = "defense",
            });

        var second = service.LevelSlots(RulesetSystem.Dnd5e, champion).Single(s => s.Id == "10.fightingStyle");
        Assert.Contains(second.Options, o => o.Id == "archery");
        Assert.Contains(service.Validate(RulesetSystem.Dnd5e, champion), i => i.Message == "Level 10 · Additional Fighting Style: Defense is already chosen at level 1.");
    }

    [Fact]
    public async Task SubclassChoices_ALoreBardsBonusProficiencies_AreThreeNewSkills_ProficientOnTheSheet()
    {
        var service = Service();
        var bard = Dnd5e("bard", 3, intel: 10, cha: 15).With("skills", new[] { "Performance", "Persuasion", "Deception" })
            .With("spells", new SpellChoice { Cantrips = ["vicious_mockery", "minor_illusion"], Known = ["charm_person", "healing_word", "sleep", "thunderwave", "heroism", "silent_image"] });
        Dictionary<string, object> Levels(params string[] skills) => new() { ["3.subclass"] = "lore", ["3.skills"] = skills };

        var slot = service.LevelSlots(RulesetSystem.Dnd5e, bard.With("levels", Levels())).Single(s => s.Id == "3.skills");
        Assert.Equal(3, slot.Picks);
        Assert.True(slot.IsSkillProficiency);
        Assert.Contains(service.Validate(RulesetSystem.Dnd5e, bard.With("levels", Levels("Religion", "Arcana", "History"))),
            i => i.Message == "Level 3 · Bonus Proficiencies: you're already proficient in Religion.");

        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, bard.With("levels", Levels("Arcana", "History", "Stealth")));
        Assert.DoesNotContain(preview.Errors, i => i.Step == "levels");
        var stats = Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats);
        Assert.True(stats.SkillModifiers.ContainsKey("Stealth"));
        Assert.Contains(preview.ClassFeatures, f => f is { Name: "Cutting Words", From: "College of Lore", Level: 3 });
    }

    [Fact]
    public async Task SubclassFeatures_DraconicResilience_RaisesHitPointsAndUnarmoredArmorClass()
    {
        var service = Service();
        var sorcerer = Dnd5e("sorcerer", 1).With("skills", new[] { "Arcana", "Persuasion" })
            .With("levels", new Dictionary<string, object> { ["1.subclass"] = "draconic", ["1.dragonAncestor"] = "red" })
            .With("spells", new SpellChoice { Cantrips = ["fire_bolt", "light", "mage_hand", "prestidigitation"], Known = ["burning_hands", "shield"] });

        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, sorcerer);

        Assert.DoesNotContain(preview.Errors, i => i.Step == "levels");
        // Human: Con 14, Dex 15. d6 + 2, + 1 for Draconic Resilience; AC 13 + Dex 2.
        Assert.Equal(6 + 2 + 1, preview.Character.MaxHp);
        Assert.Equal(13 + 2, Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats).ArmorClass);
        Assert.Contains(preview.ClassFeatures, f => f is { Name: "Dragon Ancestor", From: "Draconic Bloodline" });
    }

    [Fact]
    public async Task ClassFeatures_UnarmoredDefense_AndArcheryAsARollEffect()
    {
        var service = Service();
        var barbarian = Dnd5e("barbarian", 1, str: 15, dex: 14, con: 15, cha: 8).With("skills", new[] { "Athletics", "Survival" });
        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, barbarian);
        // Human: Dex 15, Con 16: 10 + 2 + 3.
        Assert.Equal(15, Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats).ArmorClass);

        var fighter = new Character
        {
            Id = "chars/archer", Name = "Archer", ClassLevel = "Fighter 1",
            SystemStats = new Dnd5eExtension { Level = 1, LevelUpChoices = [new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "archery" }] },
        };
        var effect = Assert.Single(CharacterClassFeatures.Effects(fighter, RulesetSystem.Dnd5e, ProgressionProvider()));
        Assert.Equal(("Archery", FeatEffectKinds.AttackBonus, 2), (effect.FeatName, effect.Effect.Kind, effect.Effect.Value));
        Assert.Equal(["ranged"], effect.Effect.Weapon);
    }

    [Fact]
    public void ClassFeatures_ListTheSubclassesUpToTheLevel_WithItsSpellsSoFar()
    {
        var cleric = new Character
        {
            Id = "chars/healer", Name = "Healer", ClassLevel = "Cleric 3",
            SystemStats = new Dnd5eExtension { Level = 3, LevelUpChoices = [new LevelUpChoiceRecord { Level = 1, Key = "subclass", Value = "life" }] },
        };

        var views = CharacterClassFeatures.Views(cleric, RulesetSystem.Dnd5e, ProgressionProvider());

        var spells = views.Single(v => v.Name == "Domain Spells");
        Assert.Equal(["bless", "cure_wounds", "lesser_restoration", "spiritual_weapon"], spells.Spells);
        Assert.Equal("Life Domain", spells.From);
        Assert.Contains(views, v => v.Name == "Channel Divinity: Preserve Life");
        Assert.DoesNotContain(views, v => v.Name == "Blessed Healer");
    }

    [Fact]
    public async System.Threading.Tasks.Task GrantStep_AddsDomainSpellsToPrepared_OnceAndOnLevelGain()
    {
        var cleric = new Character
        {
            Id = "chars/healer", Name = "Healer", ClassLevel = "Cleric 3",
            SystemStats = new Dnd5eExtension { Level = 3, LevelUpChoices = [new LevelUpChoiceRecord { Level = 1, Key = "subclass", Value = "life" }] },
        };
        var step = new CampaignVault.Rulesets.Bootstrap.Dnd5eGrantClassSpellsStep(ProgressionProvider());
        var context = new CampaignVault.Rulesets.Bootstrap.BootstrapContext { Character = cleric, ActiveSystem = RulesetSystem.Dnd5e };

        Assert.NotNull(await step.ApplyAsync(context));
        var stats = (Dnd5eExtension)cleric.SystemStats!;
        Assert.Equal(["bless", "cure_wounds", "lesser_restoration", "spiritual_weapon"], stats.Spells.Prepared);

        // Again changes nothing; level 5 brings the next pair.
        Assert.Null(await step.ApplyAsync(context));
        stats.Level = 5;
        cleric.ClassLevel = "Cleric 5";
        await step.ApplyLevelGainAsync(context);
        Assert.Equal(6, stats.Spells.Prepared.Count);
        Assert.Contains("beacon_of_hope", stats.Spells.Prepared);
    }

    [Fact]
    public void FiendPatron_ExpandsTheWarlockSpellOptions()
    {
        var service = Service();
        var warlock = new CharacterDraft { Kind = "pc", Name = "Vey", Level = 3 }
            .With("race", "human").With("class", "warlock").With("background", "acolyte");
        var spellsStep = service.Steps(RulesetSystem.Dnd5e, warlock).Single(s => s.Kind == CreationStepKinds.Spells);

        Assert.DoesNotContain(service.Options(RulesetSystem.Dnd5e, spellsStep.Key, warlock), o => o.Id == "burning_hands");
        var fiend = warlock.With("levels", new Dictionary<string, object> { ["1.subclass"] = "fiend" });
        Assert.Contains(service.Options(RulesetSystem.Dnd5e, spellsStep.Key, fiend), o => o.Id == "burning_hands");
    }

    [Fact]
    public async Task SubclassThatCasts_GetsASpellsStep_ItsSchoolsSpells_SlotsAndAbility()
    {
        WritePluginFile("classOptions", "spellblade.yaml", """
            name: spellblade
            class: fighter
            label: Spellblade
            spellcasting:
              casterType: Third
              ability: Intelligence
              list: wizard
              schools: [evocation]
              anySchoolAt: [8]
              cantripsKnown: { 3: 2 }
              spellsKnown: { 3: 3 }
            """);
        var service = Service(pluginRoot: Path.Combine(_root, "plugin"));
        var fighter = Dnd5e("fighter", 3, str: 15, intel: 14);
        var spellblade = fighter.With("levels", new Dictionary<string, object> { ["3.subclass"] = "spellblade" });
        var champion = fighter.With("levels", new Dictionary<string, object> { ["3.subclass"] = "champion" });

        Assert.DoesNotContain("spells", Keys(service.Steps(RulesetSystem.Dnd5e, champion)));
        var spellsStep = service.Steps(RulesetSystem.Dnd5e, spellblade).Single(s => s.Kind == CreationStepKinds.Spells);
        var offered = service.Options(RulesetSystem.Dnd5e, spellsStep.Key, spellblade).Select(o => o.Id).ToList();
        Assert.Contains("fire_bolt", offered);       // cantrips: any school of the list
        Assert.Contains("magic_missile", offered);   // evocation
        Assert.DoesNotContain("shield", offered);    // abjuration, before any any-school pick
        Assert.DoesNotContain("fireball", offered);  // 3rd level: beyond a level-3 third caster

        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, spellblade.With("spells", new SpellChoice
        {
            Cantrips = ["fire_bolt", "light"], Known = ["magic_missile", "burning_hands", "thunderwave"],
        }));
        var stats = Assert.IsType<Dnd5eExtension>(preview.Character.SystemStats);
        Assert.DoesNotContain(preview.Errors, i => i.Step == spellsStep.Key);
        Assert.Equal("Intelligence", stats.SpellcastingAbility);

        // Slots come at commit; a level-3 third caster has two 1st-level slots.
        var services = RulesetDataTestHelper.CreateServices();
        new ResourcePoolInitializer(services.Pools, services.Classes, services.Feats,
                new ProgressionDefinitionProvider(Path.Combine(_root, "host"), Asm, null, [Path.Combine(_root, "plugin")]))
            .InitializePools(preview.Character, RulesetSystem.Dnd5e, null);
        Assert.Equal(2, stats.ResourcePools["spell_slots_1"].Max);
        Assert.False(stats.ResourcePools.ContainsKey("spell_slots_2"));
    }

    [Fact]
    public void Validate_AboveTheRecipesMaxLevel_IsAnError_5eAndPf2eTo20()
    {
        var service = Service();

        Assert.Contains(service.Validate(RulesetSystem.Dnd5e, Wizard() with { Level = 21 }), i => i.Step == RecipeCharacterCreation.LevelIssueKey && i.Message.Contains("up to level 20"));
        Assert.DoesNotContain(service.Validate(RulesetSystem.Dnd5e, Wizard() with { Level = 20 }), i => i.Step == RecipeCharacterCreation.LevelIssueKey);
        Assert.Contains(service.Validate(RulesetSystem.Pathfinder2e, Pf2eFighter() with { Level = 21 }), i => i.Step == RecipeCharacterCreation.LevelIssueKey && i.Message.Contains("up to level 20"));
        Assert.DoesNotContain(service.Validate(RulesetSystem.Pathfinder2e, Pf2eFighter() with { Level = 20 }), i => i.Step == RecipeCharacterCreation.LevelIssueKey);
        Assert.Equal(20, service.Recipe(RulesetSystem.Dnd5e).MaxLevel("pc"));
        Assert.Null(service.Recipe(RulesetSystem.Narrative).MaxLevel("pc"));
    }

    [Fact]
    public async Task LevelChoices_ASlotThatTakesSeveral_WantsThatMany_AndALaterOneNeverRepeatsThem()
    {
        var service = Service();
        var warlock = new CharacterDraft { Kind = "pc", Name = "Vey", Level = 5 }
            .With("race", "human").With("class", "warlock").With("background", "acolyte")
            .With("abilities", new AbilityScoreChoice
            {
                Method = "standardArray",
                Scores = new() { ["Strength"] = 8, ["Dexterity"] = 14, ["Constitution"] = 13, ["Intelligence"] = 10, ["Wisdom"] = 12, ["Charisma"] = 15 },
            });
        string[] Errors(CharacterDraft draft) => [.. service.Validate(RulesetSystem.Dnd5e, draft).Where(i => !i.IsWarning && i.Step == "levels").Select(i => i.Message)];
        Dictionary<string, object> Picks(object at2, object at5) => new()
        {
            ["1.subclass"] = "fiend", ["2.invocation"] = at2, ["3.pactBoon"] = "tome", ["4.asiOrFeat"] = "Charisma", ["5.invocation"] = at5,
        };

        var slots = service.LevelSlots(RulesetSystem.Dnd5e, warlock);
        Assert.Equal(2, slots.Single(s => s.Id == "2.invocation").Picks);
        // "Learn one more" borrows the level-2 list.
        var at5 = slots.Single(s => s.Id == "5.invocation");
        Assert.Equal(1, at5.Picks);
        Assert.Contains(at5.Options, o => o.Id == "agonizingBlast");

        Assert.Equal(["Level 2 · Eldritch Invocations: choose two (you picked 1)."], Errors(warlock.With("levels", Picks(new[] { "agonizingBlast" }, "devilSight"))));
        Assert.Equal(["Level 2 · Eldritch Invocations: pick each option once."], Errors(warlock.With("levels", Picks(new[] { "agonizingBlast", "agonizingBlast" }, "devilSight")))
            .Where(e => e.Contains("once")));
        Assert.Equal(["Level 5 · Eldritch Invocations: Agonizing Blast is already chosen at level 2."],
            Errors(warlock.With("levels", Picks(new[] { "agonizingBlast", "repellingBlast" }, "agonizingBlast"))));

        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e, warlock.With("levels", Picks(new[] { "agonizingBlast", "repellingBlast" }, "devilSight")));
        Assert.DoesNotContain(preview.Errors, e => e.Step == "levels");
        var stats = preview.Character.SystemStats!;
        Assert.Equal(["agonizingBlast", "repellingBlast"], stats.LevelUpChoices.Where(c => c is { Level: 2, Key: "invocation" }).Select(c => c.Value));
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 5, Key: "invocation", Value: "devilSight" });
    }

    [Fact]
    public void LevelChoices_ASorcererChoosesTwoMetamagicOptionsAt3_AndOneMoreAt10_AsTheSrdHasIt()
    {
        var slots = Service().LevelSlots(RulesetSystem.Dnd5e, new CharacterDraft { Level = 10 }.With("class", "sorcerer"));

        Assert.Equal([("3.metamagic", 2), ("10.metamagic", 1)], slots.Where(s => s.Key == "metamagic").Select(s => (s.Id, s.Picks)));
    }

    internal static CharacterDraft Wizard() =>
        new CharacterDraft { Kind = "pc", Name = "Ilsa Venn", Concept = "A hedge scholar.", Look = "Ink-stained fingers." }
            .With("race", "human")
            .With("class", "wizard")
            .With("background", "acolyte")
            .With("abilities", new AbilityScoreChoice
            {
                Method = "standardArray",
                Scores = new() { ["Strength"] = 8, ["Dexterity"] = 13, ["Constitution"] = 14, ["Intelligence"] = 15, ["Wisdom"] = 12, ["Charisma"] = 10 },
            })
            .With("skills", new[] { "Arcana", "Investigation" })
            .With("spells", new SpellChoice
            {
                Cantrips = ["fire_bolt", "light", "mage_hand"],
                Known = ["magic_missile", "shield", "sleep", "burning_hands", "detect_magic", "mage_armor"],
                Prepared = ["magic_missile", "shield", "sleep", "mage_armor"],
            });

    private static CharacterDraft Companion(int level = 2, int? partyLevel = 2) =>
        new CharacterDraft { Kind = "companion", Name = "Brann", Level = level, PartyLevel = partyLevel }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 18, ["armorClass"] = 14, ["attacks"] = "Warhammer +4, 1d8+2" });

    [Fact]
    public async Task Companion_TextInANumberField_IsAnErrorOnTheField_NotACrash()
    {
        // What a model writes for a speed: the field says what's wrong and the player fixes it in the builder.
        var draft = new CharacterDraft { Kind = "companion", Name = "Brann Holt", Level = 2, PartyLevel = 2 }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 16, ["movement"] = "30 ft", ["stance"] = "Loyal to Aric" });

        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, draft);

        var error = Assert.Single(preview.Errors);
        Assert.Equal("statblock", error.Step);
        Assert.Equal("Speed (ft): must be a whole number.", error.Message);
    }

    [Fact]
    public async Task Companion_Preview_UsesTheStatBlockHp_KeepsTextFieldsInNotes_AndTakesTheDraftLevel()
    {
        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, Companion());

        Assert.Empty(preview.Errors);
        Assert.Empty(preview.Warnings);
        Assert.True(preview.Character.IsPartyCompanion);
        Assert.False(preview.Character.IsPc);
        Assert.Equal(18, preview.Character.MaxHp);
        Assert.Equal(14, ((Dnd5eExtension)preview.Character.SystemStats!).ArmorClass);
        Assert.Equal(2, ((Dnd5eExtension)preview.Character.SystemStats!).Level);
        Assert.Contains("Attacks: Warhammer +4, 1d8+2", preview.Character.Notes);
    }

    [Fact]
    public void Companion_StatBlockValues_AreCheckedOnTheServer()
    {
        var draft = new CharacterDraft { Kind = "companion", Name = "Brann" }
            .With("statblock", new Dictionary<string, object> { ["armorClass"] = 40, ["wingspan"] = 3 });

        var issues = Service().Validate(RulesetSystem.Dnd5e, draft).Where(i => !i.IsWarning).Select(i => i.Message).ToList();

        Assert.Contains(issues, m => m.StartsWith("Armor class: 40 is above"));
        Assert.Contains(issues, m => m.Contains("'wingspan' isn't a field"));
        Assert.Contains(issues, m => m.StartsWith("Hit points: required"));
    }

    [Fact]
    public void Companion_Skills_AreSkillNamesWithWholeNumbers_CheckedOnTheServer()
    {
        var draft = new CharacterDraft { Kind = "companion", Name = "Brann", Level = 1, PartyLevel = 1 }
            .With("statblock", new Dictionary<string, object>
            {
                ["statBlockHp"] = 10,
                ["skillModifiers"] = new Dictionary<string, object> { ["Flying"] = 2, ["Stealth"] = "+4", ["Perception"] = 40, ["Athletics"] = 3 },
            });

        var issues = Service().Validate(RulesetSystem.Dnd5e, draft).Where(i => !i.IsWarning).ToList();

        Assert.All(issues, i => Assert.Equal("statblock", i.Step));
        Assert.Equal(
            ["Skills: 'Flying' is not one of the skills.", "Skills: Stealth must be a whole number.", "Skills: Perception +40 is above 20."],
            issues.Select(i => i.Message));

        var prose = new CharacterDraft { Kind = "companion", Name = "Brann" }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 10, ["skillModifiers"] = "good at sneaking" });
        Assert.Contains(Service().Validate(RulesetSystem.Dnd5e, prose), i => i.Message.StartsWith("Skills: must be names with whole numbers"));

        // The templates' own text form reads as the object: a template copied as-is is fine.
        var template = new CharacterDraft { Kind = "companion", Name = "Brann" }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 10, ["skillModifiers"] = "Perception +5, Flying +2" });
        Assert.Equal(["Skills: 'Flying' is not one of the skills."], Service().Validate(RulesetSystem.Dnd5e, template).Where(i => !i.IsWarning).Select(i => i.Message));
    }

    [Fact]
    public void Companion_Attacks_AreRowsWithAName_AWholeToHit_AndDiceDamage()
    {
        var draft = new CharacterDraft { Kind = "companion", Name = "Brann", Level = 1, PartyLevel = 1 }
            .With("statblock", new Dictionary<string, object>
            {
                ["statBlockHp"] = 10,
                ["attacks"] = new object[]
                {
                    new Dictionary<string, object> { ["name"] = "Bite", ["bonus"] = 3, ["damage"] = "1d6+1 piercing" },
                    new Dictionary<string, object> { ["name"] = "Claw", ["bonus"] = "+2", ["damage"] = "a lot", ["reach"] = 5 },
                    new Dictionary<string, object> { ["bonus"] = 40, ["damage"] = "7" },
                },
            });

        var issues = Service().Validate(RulesetSystem.Dnd5e, draft).Where(i => !i.IsWarning).Select(i => i.Message).ToList();

        Assert.Equal(
        [
            "Attacks: Claw has 'reach', which isn't a column (name, bonus, damage, notes).",
            "Attacks: Claw's to hit must be a whole number.",
            "Attacks: Claw's damage must start with dice or a number, like 1d6+2 piercing.",
            "Attacks: row 3 needs its attack.",
            "Attacks: row 3's to hit +40 is above 20.",
        ], issues);

        var prose = new CharacterDraft { Kind = "companion", Name = "Brann" }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 10, ["attacks"] = 7 });
        Assert.Contains(Service().Validate(RulesetSystem.Dnd5e, prose), i => i.Message.StartsWith("Attacks: must be a list of rows"));

        var many = new CharacterDraft { Kind = "companion", Name = "Brann" }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 10, ["attacks"] = string.Join("; ", Enumerable.Repeat("Bite +3, 1d6", 7)) });
        Assert.Equal(["Attacks: 7 rows, at most 6."], Service().Validate(RulesetSystem.Dnd5e, many).Where(i => !i.IsWarning).Select(i => i.Message));
    }

    [Fact]
    public async Task Companion_Attacks_ReadTheTemplatesText_AndGoToTheNotesInTheSameForm()
    {
        const string text = "Shortsword +4, 1d6+2 piercing, reach 5 ft.; Longbow +4, 1d8+2 piercing, range 150/600 ft., two-handed";
        StatBlockColumn[] columns =
        [
            new() { Key = "name" }, new() { Key = "bonus", Type = "int" }, new() { Key = "damage", Type = "dice" }, new() { Key = "notes" },
        ];
        Assert.True(StatRowsText.TryParse(text, columns, out var rows));
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("Longbow", rows[1].GetProperty("name").GetString());
        Assert.Equal(4, rows[1].GetProperty("bonus").GetInt32());
        Assert.Equal("1d8+2 piercing", rows[1].GetProperty("damage").GetString());
        Assert.Equal("range 150/600 ft., two-handed", rows[1].GetProperty("notes").GetString());
        Assert.Equal(text, StatRowsText.Format(rows, columns));

        var draft = new CharacterDraft { Kind = "companion", Name = "Wren", Level = 1, PartyLevel = 1 }
            .With("statblock", new Dictionary<string, object>
            {
                ["statBlockHp"] = 16,
                ["attacks"] = new object[] { new Dictionary<string, object> { ["name"] = "Bite", ["bonus"] = 3, ["damage"] = "1d6+1 piercing" } },
                ["traits"] = "Keen nose.",
            });

        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, draft);

        Assert.Empty(preview.Errors);
        Assert.Contains("Attacks: Bite +3, 1d6+1 piercing. Traits: Keen nose.", preview.Character.Notes);
    }

    [Fact]
    public async Task Companion_CreatureType_IsOneOfTheSystemsTypes_AndGoesToTheNotesInItsSpelling()
    {
        var robot = new CharacterDraft { Kind = "companion", Name = "Cog", Level = 1, PartyLevel = 1 }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 10, ["creatureType"] = "Robot" });
        var error = Assert.Single(Service().Validate(RulesetSystem.Dnd5e, robot).Where(i => !i.IsWarning));
        Assert.StartsWith("Creature type: 'Robot' isn't one of: Aberration, Beast,", error.Message);

        var dog = new CharacterDraft { Kind = "companion", Name = "Rook", Level = 1, PartyLevel = 1 }
            .With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 10, ["creatureType"] = "beast" });
        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, dog);
        Assert.Empty(preview.Errors);
        Assert.Contains("Creature type: Beast.", preview.Character.Notes);
    }

    [Fact]
    public async Task Companion_Skills_AreStoredInTheSystemsSpelling_AndTheChallengeRatingGoesToTheNotes()
    {
        var draft = new CharacterDraft { Kind = "companion", Name = "Wren", Level = 1, PartyLevel = 1 }
            .With("statblock", new Dictionary<string, object>
            {
                ["statBlockHp"] = 16, ["challengeRating"] = "1/2",
                ["skillModifiers"] = new Dictionary<string, object> { ["perception"] = 5, ["Sleight of hand"] = 4 },
            });

        var preview = await Service().PreviewAsync(RulesetSystem.Dnd5e, draft);

        Assert.Empty(preview.Errors);
        var skills = ((Dnd5eExtension)preview.Character.SystemStats!).SkillModifiers;
        Assert.Equal(5, skills["Perception"]);
        Assert.Equal(4, skills["Sleight of Hand"]);
        Assert.Equal(2, skills.Count);
        Assert.Contains("Challenge rating: 1/2", preview.Character.Notes);
    }

    [Fact]
    public async Task Companion_Templates_AreTheArchetypes_AndACopiedOneBuildsWithItsHpNotAFormula()
    {
        var service = Service();
        var draft = new CharacterDraft { Kind = "companion" };

        var options = service.Options(RulesetSystem.Dnd5e, "statblock", draft);
        Assert.Equal(["Acolyte", "Guard", "Hawk", "Mastiff", "Riding Horse", "Scout", "Thug", "Warhorse"], options.Select(o => o.Id));

        var warhorse = options.Single(o => o.Id == "Warhorse");
        var values = warhorse.Values!.ToDictionary(kv => kv.Key, kv => (object)(int.TryParse(kv.Value, out var n) ? n : kv.Value));
        var preview = await service.PreviewAsync(RulesetSystem.Dnd5e,
            new CharacterDraft { Kind = "companion", Name = "Smoke", Level = 1, PartyLevel = 1 }.With("statblock", values));

        Assert.Empty(preview.Errors);
        Assert.Equal(19, preview.Character.MaxHp);
        Assert.Contains("Trampling Charge", preview.Character.Notes);

        // Every template copied as the server lists it (numbers and skills as text, the way a model would copy them) builds.
        foreach (var option in options)
        {
            var copied = await service.PreviewAsync(RulesetSystem.Dnd5e,
                new CharacterDraft { Kind = "companion", Name = option.Label, Level = 1, PartyLevel = 1 }
                    .With("statblock", option.Values!.ToDictionary(kv => kv.Key, kv => (object)(int.TryParse(kv.Value, out var n) ? n : kv.Value))));
            Assert.True(copied.Errors.Count == 0, option.Id + ": " + string.Join("; ", copied.Errors.Select(e => e.Message)));
        }

        var scout = await service.PreviewAsync(RulesetSystem.Dnd5e, new CharacterDraft { Kind = "companion", Name = "Scout", Level = 1, PartyLevel = 1 }
            .With("statblock", options.Single(o => o.Id == "Scout").Values!.ToDictionary(kv => kv.Key, kv => (object)(int.TryParse(kv.Value, out var n) ? n : kv.Value))));
        Assert.Equal(6, ((Dnd5eExtension)scout.Character.SystemStats!).SkillModifiers["Stealth"]);
        Assert.Contains("Challenge rating: 1/2", scout.Character.Notes);
    }

    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, false)]
    [InlineData(1, 2, false)]
    [InlineData(4, 2, true)]
    [InlineData(1, 3, true)]
    public void Companion_PowerCheck_WarnsOutsideOneLevelOfTheParty_AndNeverBlocks(int level, int party, bool warns)
    {
        var issues = Service().Validate(RulesetSystem.Dnd5e, Companion(level, party));

        Assert.Equal(warns, issues.Any(i => i.IsWarning));
        Assert.DoesNotContain(issues, i => !i.IsWarning);
    }

    [Fact]
    public void Companion_PowerCheck_IsSilentWithoutAPartyLevel()
    {
        Assert.Empty(Service().Validate(RulesetSystem.Dnd5e, Companion(9, null)));
    }

    // ---- PF2e (Phase 6) ----

    public static CharacterDraft Pf2eFighter() => new CharacterDraft { Kind = "pc", Name = "Brakka" }
        .With("ancestry", "dwarf")
        .With("heritage", "rock_dwarf")
        .With("background", "martial_disciple")
        .With("backgroundSkill", "Athletics")
        .With("class", "fighter")
        .With("ancestryBoosts", new[] { "Strength" })
        .With("backgroundBoosts", new[] { "Strength", "Dexterity" })
        .With("keyAbility", new[] { "Strength" })
        .With("boosts", new[] { "Strength", "Dexterity", "Constitution", "Wisdom" })
        .With("skills", new[] { "Intimidation", "Medicine", "Survival", "Society" })
        .With("ancestryFeats", new[] { "dwarven_lore" })
        .With("classFeats", new[] { "reactive_shield" });

    private CharacterDraft Pf2eWizard(CharacterCreationService service)
    {
        var draft = Pf2eWizardWithoutSpells();
        var spells = service.Options(RulesetSystem.Pathfinder2e, "spells", draft);
        return draft.With("spells", new SpellChoice
        {
            Cantrips = [.. spells.Where(o => o.Group == SpellGroups.Cantrips).Take(10).Select(o => o.Id)],
            Known = [.. spells.Where(o => o.Group == SpellGroups.Known).Take(5).Select(o => o.Id)],
        });
    }

    /// <summary>An elf wizard with everything but spells (pick them from the spells step's options).</summary>
    public static CharacterDraft Pf2eWizardWithoutSpells() =>
        new CharacterDraft { Kind = "pc", Name = "Ilsa" }
            .With("ancestry", "elf")
            .With("heritage", "ancient_elf")
            .With("background", "acolyte")
            .With("class", "wizard")
            .With("classFeatures", new Dictionary<string, object> { ["1.school"] = "schoolOfBattleMagic", ["1.thesis"] = "spellSubstitution" })
            .With("ancestryBoosts", new[] { "Wisdom" })
            .With("backgroundBoosts", new[] { "Intelligence", "Constitution" })
            .With("keyAbility", new[] { "Intelligence" })
            .With("boosts", new[] { "Intelligence", "Dexterity", "Constitution", "Wisdom" })
            .With("skills", new[] { "Crafting", "Diplomacy", "Medicine", "Nature", "Occultism", "Society" })
            .With("ancestryFeats", new[] { "ancestral_longevity" });

    public static CharacterDraft Pf2eRogue(string racket) => new CharacterDraft { Kind = "pc", Name = "Sly" }
        .With("ancestry", "human")
        .With("heritage", "skilled_human")
        .With("background", "acolyte")
        .With("class", "rogue")
        .With("classFeatures", new Dictionary<string, object> { ["1.racket"] = racket });

    [Fact]
    public async Task Pf2e_ARoguesRacket_IsAskedBeforeTheSkills_TrainsItsSkill_AndCanChangeTheKeyAttribute()
    {
        var service = Service();
        var noRacket = new CharacterDraft { Kind = "pc" }.With("class", "rogue");

        var keys = Keys(service.Steps(RulesetSystem.Pathfinder2e, noRacket));
        Assert.True(keys.IndexOf("class") < keys.IndexOf("classFeatures") && keys.IndexOf("classFeatures") < keys.IndexOf("skills"));
        Assert.DoesNotContain("classFeatures", Keys(service.Steps(RulesetSystem.Pathfinder2e, new CharacterDraft { Kind = "pc" })));
        Assert.Equal(["mastermind", "ruffian", "scoundrel", "thief"], service.Options(RulesetSystem.Pathfinder2e, "classFeatures", noRacket).Select(o => o.Id));
        Assert.Contains(service.Validate(RulesetSystem.Pathfinder2e, noRacket), i => i is { Step: "classFeatures", Message: "Level 1 · Rogue's Racket: choose one." });

        // A thief is trained in Thievery: the skills step doesn't offer it, and the key attribute stays Dexterity.
        var thief = Pf2eRogue("thief");
        Assert.DoesNotContain(service.Options(RulesetSystem.Pathfinder2e, "skills", thief), o => o.Id == "Thievery");
        Assert.Equal(["Dexterity"], service.Options(RulesetSystem.Pathfinder2e, "keyAbility", thief).Select(o => o.Id));
        // A ruffian may use Strength; a mastermind picks one more skill.
        Assert.Equal(["Dexterity", "Strength"], service.Options(RulesetSystem.Pathfinder2e, "keyAbility", Pf2eRogue("ruffian")).Select(o => o.Id));
        var ctx = service.Context(RulesetSystem.Pathfinder2e, thief);
        var skills = service.Recipe(RulesetSystem.Pathfinder2e).AllSteps("pc").Single(s => s.Key == "skills");
        Assert.Equal(ctx.Count(skills, null) + 1, service.Context(RulesetSystem.Pathfinder2e, Pf2eRogue("mastermind")).Count(skills, null));

        var preview = await service.PreviewAsync(RulesetSystem.Pathfinder2e, thief);
        var stats = Assert.IsType<Pf2eExtension>(preview.Character.SystemStats);
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 1, Key: "racket", Value: "thief" });
        Assert.DoesNotContain(preview.Errors, e => e.Step == "classFeatures");
    }

    [Fact]
    public async Task Pf2e_ALevel5Fighter_RaisesItsSkillsAndAttributes_WithinTheRules()
    {
        var service = Service();
        Dictionary<string, object> Levels(string at3, string at5, params string[] boosts) => new()
        {
            ["3.skillIncrease"] = at3, ["5.skillIncrease"] = at5, ["5.attributeBoosts"] = boosts,
        };
        var fighter = Pf2eFighter() with { Level = 5 };
        string[] Errors(CharacterDraft draft) => [.. service.Validate(RulesetSystem.Pathfinder2e, draft).Where(i => !i.IsWarning && i.Step == "levels").Select(i => i.Message)];

        var slots = service.LevelSlots(RulesetSystem.Pathfinder2e, fighter, "levels");
        // In level order, then the class table's order.
        Assert.Equal(["3.skillIncrease", "5.attributeBoosts", "5.skillIncrease"], slots.Select(s => s.Id));
        Assert.Equal(4, slots.Single(s => s.Id == "5.attributeBoosts").Picks);
        Assert.Contains(slots[0].Options, o => o.Id == "Athletics");
        Assert.Empty(service.LevelSlots(RulesetSystem.Pathfinder2e, Pf2eFighter(), "levels"));

        // Athletics is trained (the background): expert at 3, but master waits for level 7.
        Assert.Equal(["Level 5 · Skill Increase: Athletics is already expert; master needs level 7."],
            Errors(fighter.With("levels", Levels("Athletics", "Athletics", "Strength", "Dexterity", "Constitution", "Wisdom"))));
        Assert.Equal(["Level 5 · Attribute Boosts: choose four (you picked 2)."],
            Errors(fighter.With("levels", Levels("Athletics", "Survival", "Strength", "Dexterity"))));

        var preview = await service.PreviewAsync(RulesetSystem.Pathfinder2e,
            fighter.With("levels", Levels("Athletics", "Survival", "Strength", "Dexterity", "Constitution", "Wisdom")));
        Assert.DoesNotContain(preview.Errors, e => e.Step == "levels");
        var stats = Assert.IsType<Pf2eExtension>(preview.Character.SystemStats);
        Assert.Equal(Pf2eProficiencyRank.Expert, stats.SkillProficiencies["Athletics"]);
        Assert.Equal(Pf2eProficiencyRank.Expert, stats.SkillProficiencies["Survival"]);
        // Strength was +4 at level 1, so its level-5 boost is a partial one; Dexterity goes from +2 to +3.
        Assert.Equal(4, stats.StrengthMod);
        Assert.Equal(3, stats.DexterityMod);
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 5, Key: "attributeBoosts", Value: "Dexterity" });
        // Creation picks are level-1 choices whatever level the character is built at (the bootstrap reads them there).
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 1, Key: "skills", Value: "Medicine" });
        Assert.Equal(Pf2eProficiencyRank.Untrained, stats.SkillProficiencies.GetValueOrDefault("Thievery"));
    }

    [Fact]
    public void Pf2e_FeatPrerequisites_TheDataCanCheck_AreChecked_AgainstSkillRanksAndClassFeatures()
    {
        var service = Service();
        var rogue = Pf2eRogue("thief") with { Level = 2 };
        string[] Errors(CharacterDraft draft, string step) => [.. service.Validate(RulesetSystem.Pathfinder2e, draft).Where(i => !i.IsWarning && i.Step == step).Select(i => i.Message)];

        // Brutal Beating is the ruffian's.
        Assert.Equal(["Brutal Beating needs Ruffian."], Errors(rogue.With("classFeats", new[] { "brutal_beating" }), "classFeats").Where(e => e.Contains("needs")));
        Assert.DoesNotContain(Errors((Pf2eRogue("ruffian") with { Level = 2 }).With("classFeats", new[] { "brutal_beating" }), "classFeats"), e => e.Contains("needs"));

        // Armored Stealth needs expert in Stealth: a rogue is trained in it, and its level-2 skill increase makes it expert.
        var armored = rogue.With("skillFeats", new[] { "armored_stealth" });
        Assert.Contains("Armored Stealth needs expert in Stealth.", Errors(armored, "skillFeats"));
        Assert.DoesNotContain(Errors(armored.With("levels", new Dictionary<string, object> { ["2.skillIncrease"] = "Stealth" }), "skillFeats"), e => e.Contains("needs"));
    }

    [Fact]
    public void Pf2e_Steps_FollowTheRecipe_AndHideWhatTheClassHasNothingToPickFor()
    {
        var service = Service();
        var draft = new CharacterDraft { Kind = "pc" };

        Assert.Equal(
            ["ancestry", "heritage", "background", "class", "ancestryBoosts", "backgroundBoosts", "keyAbility", "boosts", "skills",
             "ancestryFeats", "classFeats", "skillFeats", "generalFeats", "spells", "identity"],
            Keys(service.Steps(RulesetSystem.Pathfinder2e, draft)));

        // A level-1 fighter: a class feat, no skill or general feat (the background gives the skill feat), no spells.
        Assert.Equal(
            ["ancestry", "heritage", "background", "backgroundSkill", "class", "ancestryBoosts", "backgroundBoosts", "keyAbility", "boosts",
             "skills", "ancestryFeats", "classFeats", "identity"],
            Keys(service.Steps(RulesetSystem.Pathfinder2e, Pf2eFighter())));

        // A level-1 wizard: no class feat, spells.
        var wizard = Keys(service.Steps(RulesetSystem.Pathfinder2e, Pf2eWizard(service)));
        Assert.DoesNotContain("classFeats", wizard);
        Assert.DoesNotContain("backgroundSkill", wizard);
        Assert.Contains("spells", wizard);

        // A level-3 fighter has class feats from 1 and 2, a skill feat from 2 and a general feat from 3.
        var level3 = Pf2eFighter() with { Level = 3 };
        var ctx = service.Context(RulesetSystem.Pathfinder2e, level3);
        var steps = service.Steps(RulesetSystem.Pathfinder2e, level3);
        Assert.Equal(2, ctx.Count(steps.Single(s => s.Key == "classFeats"), null));
        Assert.Equal(1, ctx.Count(steps.Single(s => s.Key == "skillFeats"), null));
        Assert.Equal(1, ctx.Count(steps.Single(s => s.Key == "generalFeats"), null));
    }

    [Fact]
    public void Pf2e_Options_FollowTheAncestryBackgroundAndClass()
    {
        var service = Service();
        var fighter = Pf2eFighter();

        Assert.Contains("rock_dwarf", service.Options(RulesetSystem.Pathfinder2e, "heritage", fighter).Select(o => o.Id));
        Assert.Equal("Rock Dwarf", service.Options(RulesetSystem.Pathfinder2e, "heritage", fighter).Single(o => o.Id == "rock_dwarf").Label);
        Assert.Equal(["Acrobatics", "Athletics"], service.Options(RulesetSystem.Pathfinder2e, "backgroundSkill", fighter).Select(o => o.Id));
        // The dwarf boosts Constitution and Wisdom already, so its free boost goes elsewhere.
        Assert.Equal(["Strength", "Dexterity", "Intelligence", "Charisma"], service.Options(RulesetSystem.Pathfinder2e, "ancestryBoosts", fighter).Select(o => o.Id));
        Assert.Equal(["Strength", "Dexterity"], service.Options(RulesetSystem.Pathfinder2e, "keyAbility", fighter).Select(o => o.Id));
        // Trained skills: not the background's pick (Athletics).
        Assert.DoesNotContain("Athletics", service.Options(RulesetSystem.Pathfinder2e, "skills", fighter).Select(o => o.Id));

        var classFeats = service.Options(RulesetSystem.Pathfinder2e, "classFeats", fighter);
        Assert.Contains(classFeats, o => o.Id == "reactive_shield");
        Assert.All(classFeats, o => Assert.Equal("level 1", o.Group));
        var ancestryFeats = service.Options(RulesetSystem.Pathfinder2e, "ancestryFeats", fighter).Select(o => o.Id).ToList();
        Assert.Contains("dwarven_lore", ancestryFeats);
        Assert.DoesNotContain("elven_lore", ancestryFeats);
    }

    [Fact]
    public void Pf2e_SkillCount_IsTheClassesPlusTheIntelligenceModifierSoFar()
    {
        var service = Service();
        var wizard = Pf2eWizard(service);
        var skills = service.Steps(RulesetSystem.Pathfinder2e, wizard).Single(s => s.Key == "skills");

        // Wizard: 2 more skills, plus Int +4 (elf +1, background +1, key +1, free +1).
        Assert.Equal(6, service.Context(RulesetSystem.Pathfinder2e, wizard).Count(skills, null));
        var lessInt = wizard.With("boosts", new[] { "Strength", "Dexterity", "Constitution", "Wisdom" });
        Assert.Equal(5, service.Context(RulesetSystem.Pathfinder2e, lessInt).Count(skills, null));
    }

    [Fact]
    public async Task Pf2e_Fighter_PreviewHasTheModifiersHpSkillsAndFeatsTheRulesGive()
    {
        var preview = await Service().PreviewAsync(RulesetSystem.Pathfinder2e, Pf2eFighter());

        Assert.Empty(preview.Errors);
        var stats = Assert.IsType<Pf2eExtension>(preview.Character.SystemStats);
        // Strength: ancestry free, background, key, free. Constitution and Wisdom: the dwarf's own, plus a free boost.
        Assert.Equal((4, 2, 2, 0, 2, -1), (stats.StrengthMod, stats.DexterityMod, stats.ConstitutionMod, stats.IntelligenceMod, stats.WisdomMod, stats.CharismaMod));
        Assert.Equal("dwarf", stats.Ancestry);
        Assert.Equal("rock_dwarf", stats.Heritage);
        Assert.Equal("martial_disciple", stats.Background);
        Assert.Equal("Fighter 1", preview.Character.ClassLevel);
        // Dwarf 10 + fighter 10 + Con 2.
        Assert.Equal(22, preview.Character.MaxHp);
        Assert.Equal(
            ["Athletics", "Intimidation", "Medicine", "Society", "Survival", "Warfare Lore"],
            stats.SkillProficiencies.Where(kv => kv.Value == Pf2eProficiencyRank.Trained).Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(7, stats.SkillModifiers["Athletics"]); // Str 4 + level 1 + trained 2
        Assert.Equal(3, stats.SkillModifiers["Warfare Lore"]); // Int 0 + 1 + 2
        Assert.False(stats.SkillProficiencies.ContainsKey("Acrobatics")); // not every skill trained, as for a character made without picks
        Assert.Equal(["dwarven_lore"], stats.AncestryFeats);
        Assert.Equal(["reactive_shield"], stats.ClassFeats);
        Assert.Empty(stats.SkillFeats); // Martial Disciple's skill feat is a choice (Cat Fall or Quick Jump), not set
    }

    [Fact]
    public async Task Pf2e_Wizard_PreviewHasTheModifiersHpSkillsBackgroundFeatAndSpells()
    {
        var service = Service();
        var draft = Pf2eWizard(service);
        var preview = await service.PreviewAsync(RulesetSystem.Pathfinder2e, draft);

        Assert.Empty(preview.Errors);
        var stats = Assert.IsType<Pf2eExtension>(preview.Character.SystemStats);
        // Elf: Dex and Int +1, Con -1.
        Assert.Equal((0, 2, 1, 4, 2, 0), (stats.StrengthMod, stats.DexterityMod, stats.ConstitutionMod, stats.IntelligenceMod, stats.WisdomMod, stats.CharismaMod));
        Assert.Equal(13, preview.Character.MaxHp); // elf 6 + wizard 6 + Con 1
        Assert.Equal(
            ["Arcana", "Crafting", "Diplomacy", "Medicine", "Nature", "Occultism", "Religion", "Scribing Lore", "Society"],
            stats.SkillProficiencies.Where(kv => kv.Value == Pf2eProficiencyRank.Trained).Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["student_of_the_canon"], stats.SkillFeats);
        Assert.Equal(10, stats.Spells.Cantrips.Count);
        Assert.Equal(5, stats.Spells.Known.Count);
        Assert.Equal(draft.Get<SpellChoice>("spells")!.Known, stats.Spells.Known);
    }

    [Fact]
    public void Pf2e_Boosts_AFreeAncestryBoostWhereTheAncestryBoostsAlready_AndNoBackgroundBoostFromItsPair_AreErrors()
    {
        var service = Service();
        var draft = Pf2eFighter()
            .With("ancestryBoosts", new[] { "Constitution" })
            .With("backgroundBoosts", new[] { "Wisdom", "Charisma" })
            .With("boosts", new[] { "Strength", "Strength", "Dexterity", "Wisdom" });

        var errors = service.Validate(RulesetSystem.Pathfinder2e, draft).Where(i => !i.IsWarning).ToList();

        Assert.Contains(errors, e => e.Step == "ancestryBoosts" && e.Message == "Dwarf already boosts Constitution; a free boost goes to another attribute.");
        Assert.Contains(errors, e => e.Step == "backgroundBoosts" && e.Message == "One of Martial Disciple's boosts goes to Strength or Dexterity.");
        Assert.Contains(errors, e => e.Step == "boosts" && e.Message.StartsWith("Picked more than once: Strength"));
    }

    [Fact]
    public void Pf2e_Feats_FromAnotherClassAncestryOrALaterLevel_SayWhy()
    {
        var service = Service();
        var draft = Pf2eFighter()
            .With("classFeats", new[] { "reach_spell" })
            .With("ancestryFeats", new[] { "elven_lore" });

        var errors = service.Validate(RulesetSystem.Pathfinder2e, draft).Where(i => !i.IsWarning).ToList();

        Assert.Contains(errors, e => e.Step == "classFeats" && e.Message == "Reach Spell isn't a Fighter feat.");
        Assert.Contains(errors, e => e.Step == "ancestryFeats" && e.Message == "Elven Lore isn't a Dwarf feat.");
        Assert.Contains(errors, e => e.Step == "ancestryFeats" && e.Message.StartsWith("Not among the options"));
    }

    [Fact]
    public void Pf2e_Fighter_IsTrainedInAcrobaticsOrAthletics_FromItsPicksOrItsBackground()
    {
        var service = Service();
        // Martial Disciple trains Athletics, so the picks needn't include either.
        Assert.Empty(service.Validate(RulesetSystem.Pathfinder2e, Pf2eFighter()).Where(i => !i.IsWarning));

        // The Athletics picked for Martial Disciple stays in the draft, but isn't one of Acolyte's options, so it doesn't count.
        var acolyte = Pf2eFighter().With("background", "acolyte").With("backgroundBoosts", new[] { "Wisdom", "Strength" });
        Assert.Contains(service.Validate(RulesetSystem.Pathfinder2e, acolyte),
            e => e.Step == "skills" && e.Message == "A Fighter is trained in Acrobatics or Athletics: pick one of them.");
        Assert.DoesNotContain(service.Validate(RulesetSystem.Pathfinder2e, acolyte.With("skills", new[] { "Acrobatics", "Medicine", "Survival", "Society" })),
            e => e.Step == "skills");
    }

    [Fact]
    public async Task Pf2e_Companion_IsAStatBlock_OfModifiersSavesPerceptionAndStrikes()
    {
        var service = Service();
        var draft = new CharacterDraft { Kind = "companion", Name = "Ash", Level = 2, PartyLevel = 2 }
            .With("statblock", new Dictionary<string, object>
            {
                ["creatureType"] = "animal",
                ["statBlockHp"] = 30,
                ["armorClass"] = 18,
                ["movement"] = 40,
                ["strengthMod"] = 3,
                ["dexterityMod"] = 4,
                ["savingThrowModifiers"] = "Fortitude +8, Reflex +10, Will +6",
                ["skillModifiers"] = "Perception +8, Stealth +9",
                ["attacks"] = "Jaws +10, 1d8+3 piercing, Knockdown",
                ["stance"] = "Follows the ranger.",
            });

        var schema = Assert.Single(service.Steps(RulesetSystem.Pathfinder2e, draft)).Schema;
        Assert.Equal("companion", schema);
        var preview = await service.PreviewAsync(RulesetSystem.Pathfinder2e, draft);

        Assert.Empty(preview.Errors);
        var stats = Assert.IsType<Pf2eExtension>(preview.Character.SystemStats);
        Assert.True(preview.Character.IsPartyCompanion);
        Assert.Equal(30, stats.StatBlockHp);
        Assert.Equal(18, stats.ArmorClass);
        Assert.Equal((3, 4), (stats.StrengthMod, stats.DexterityMod));
        Assert.Equal(2, stats.Level);
        Assert.Equal(new Dictionary<string, int> { ["Fortitude"] = 8, ["Reflex"] = 10, ["Will"] = 6 }, stats.SavingThrowModifiers);
        Assert.Equal(8, stats.SkillModifiers["Perception"]);
        Assert.Equal(9, stats.SkillModifiers["Stealth"]);
        // Only what the stat block lists: not every skill trained, as for a character made without picks.
        Assert.Equal(["Perception", "Stealth"], stats.SkillModifiers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(3, stats.SavingThrowModifiers.Count);
        Assert.Contains("Creature type: Animal.", preview.Character.Notes);
        Assert.Contains("Strikes: Jaws +10, 1d8+3 piercing, Knockdown.", preview.Character.Notes);

        var bad = draft.With("statblock", new Dictionary<string, object> { ["statBlockHp"] = 30, ["savingThrowModifiers"] = "Dexterity +3", ["creatureType"] = "Monstrosity" });
        var errors = service.Validate(RulesetSystem.Pathfinder2e, bad).Where(i => !i.IsWarning).Select(i => i.Message).ToList();
        Assert.Contains("Saves: 'Dexterity' is not one of the saves.", errors);
        Assert.Contains(errors, e => e.StartsWith("Creature type: 'Monstrosity' isn't one of"));
    }

    [Fact]
    public void Reads_SayWhichStepsAStepDependsOn_ForTheClientToClearAndRefetch()
    {
        var service = Service();
        IReadOnlyList<string> Reads(string system, string key)
        {
            var steps = service.Recipe(system).AllSteps("pc");
            return RecipeCharacterCreation.Reads(steps.Single(s => s.Key == key), steps);
        }

        Assert.Equal(["class", "background"], Reads(RulesetSystem.Dnd5e, "skills"));
        // A subclass can add spells or cast from another class's list, so the level choices count too.
        Assert.Equal(["class", "levels"], Reads(RulesetSystem.Dnd5e, "spells"));
        Assert.Empty(Reads(RulesetSystem.Dnd5e, "race"));
        // A god, patron or bloodline narrows the class's choices, so picking one refetches them.
        Assert.Equal(["class", "deity", "patron", "lineage"], Reads(RulesetSystem.Dnd5e, "levels"));
        Assert.Equal(["class", "deity", "patron", "lineage"], Reads(RulesetSystem.Pathfinder2e, "classFeatures"));

        Assert.Equal(["ancestry"], Reads(RulesetSystem.Pathfinder2e, "heritage"));
        Assert.Equal(["background"], Reads(RulesetSystem.Pathfinder2e, "backgroundSkill"));
        Assert.Equal(["ancestry"], Reads(RulesetSystem.Pathfinder2e, "ancestryBoosts"));
        // A racket can make Strength the key attribute.
        Assert.Equal(["class", "classFeatures"], Reads(RulesetSystem.Pathfinder2e, "keyAbility"));
        Assert.Equal(["ancestry", "class"], Reads(RulesetSystem.Pathfinder2e, "ancestryFeats"));
        // The count is the class's plus the Intelligence modifier, which the ancestry and every boost step change.
        Assert.Equal(
            ["ancestry", "background", "backgroundSkill", "class", "classFeatures", "ancestryBoosts", "backgroundBoosts", "keyAbility", "boosts"],
            Reads(RulesetSystem.Pathfinder2e, "skills"));
    }

    private CharacterCreationService Service(string? pluginRoot = null)
    {
        var host = Path.Combine(_root, "host");
        Directory.CreateDirectory(host);
        var races = new RaceDefinitionProvider(host, Asm);
        var classes = new ClassDefinitionProvider(host, Asm);
        var backgrounds = new BackgroundDefinitionProvider(host, Asm);
        var spells = new SpellDefinitionProvider(host, Asm);
        var creatures = new CreatureDefinitionProvider(host, Asm);
        var roll = new DefaultRollService(new Random(42));
        var roots = pluginRoot is null ? (IReadOnlyList<string>)[] : [pluginRoot];
        var progressions = new ProgressionDefinitionProvider(host, Asm, null, roots);
        var powers = new NamedPowerProvider(host, Asm, null, roots);
        var selector = new RulesetModuleSelector(
        [
            new Dnd5eRulesetResolver(roll, races, classes, backgrounds, spells, creatures, progressionProvider: progressions),
            new Pf2eRulesetResolver(roll, races, spells, creatures, classProvider: classes, backgroundProvider: backgrounds,
                progressionProvider: progressions),
            new NarrativeRulesetResolver(roll),
        ]);
        var feats = new FeatDefinitionProvider(host, Asm);
        var sources = new CreationSources(
            races, classes, backgrounds, feats, spells, creatures, progressions, powers);
        var recipes = new CreationRecipeProvider(host, Asm, null, pluginRoot is null ? [] : [pluginRoot]);
        IRecipeValidator[] validators =
        [
            new PickOneOptionValidator(), new PickNOptionValidator(), new PickNCountValidator(), new AbilityScoresMethodValidator(),
            new StandardArrayValidator(), new PointBuyValidator(), new RollRangeValidator(), new SpellsCountForLevelValidator(),
            new StatBlockFieldsValidator(recipes), new CompanionPowerValidator(), new FeatPrerequisitesValidator(feats),
            new Pf2eBoostsValidator(), new Pf2eFeatEligibilityValidator(feats), new Pf2eClassSkillsValidator(),
        ];
        return new CharacterCreationService(selector, recipes, sources, validators, new CharacterBootstrapOrchestrator(selector));
    }

    private ProgressionDefinitionProvider ProgressionProvider()
    {
        var host = Path.Combine(_root, "host");
        Directory.CreateDirectory(host);
        return new ProgressionDefinitionProvider(host, Asm);
    }

    private static List<string> Keys(IEnumerable<CreationStep> steps) => [.. steps.Select(s => s.Key)];
}
