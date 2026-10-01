using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
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

    [Fact]
    public void Steps_ForNarrative_AreJustIdentity()
    {
        var steps = Service().Steps(RulesetSystem.Narrative, new CharacterDraft { Kind = "pc" });

        var step = Assert.Single(steps);
        Assert.Equal(CreationStepKinds.Identity, step.Kind);
    }

    [Fact]
    public void Options_ForSkills_AreTheClassList_WithoutTheBackgroundsSkills_AndCountComesFromTheClass()
    {
        var service = Service();
        var draft = new CharacterDraft().With("class", "wizard").With("background", "sage");

        var options = service.Options(RulesetSystem.Dnd5e, "skills", draft).Select(o => o.Id).ToList();
        var skillsStep = service.Steps(RulesetSystem.Dnd5e, draft).Single(s => s.Key == "skills");

        Assert.Equal(["Insight", "Investigation", "Medicine", "Religion"], options);
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

        var tooMany = service.Validate(RulesetSystem.Dnd5e, Wizard().With("skills", new[] { "Insight", "Investigation", "Medicine" }));
        var excluded = service.Validate(RulesetSystem.Dnd5e, Wizard().With("skills", new[] { "Insight", "Arcana" }));

        Assert.Contains(tooMany, i => i.Step == "skills" && i.Message.Contains("Too many: pick 2, not 3"));
        Assert.Contains(excluded, i => i.Step == "skills" && i.Message.Contains("Arcana"));
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
        Assert.Equal(3 + 2, stats.SkillModifiers["Arcana"]);        // background (sage)
        Assert.Equal(3 + 2, stats.SavingThrowModifiers["Intelligence"]);
        Assert.Equal(["fire_bolt", "light", "mage_hand"], stats.Spells.Cantrips);
        Assert.Equal(6, stats.Spells.Known.Count);
        Assert.Contains(stats.LevelUpChoices, c => c is { Level: 1, Key: "skills", Value: "Insight" });
        Assert.True(preview.Character.IsPc);
    }

    [Fact]
    public void Patch_FromAPluginRoot_AddsAStep_WhereItsAfterSaysSo()
    {
        WritePluginRecipe("deity_patch.yaml", """
            patches: pc
            steps+:
              - { key: deity, kind: pickOne, after: background, prompt: Deity }
            """);

        var steps = Service(pluginRoot: Path.Combine(_root, "plugin")).Steps(RulesetSystem.Dnd5e, new CharacterDraft());

        Assert.Equal(["race", "class", "background", "deity", "abilities", "skills", "spells", "identity"], Keys(steps));
    }

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

    internal static CharacterDraft Wizard() =>
        new CharacterDraft { Kind = "pc", Name = "Ilsa Venn", Concept = "A hedge scholar.", Look = "Ink-stained fingers." }
            .With("race", "human")
            .With("class", "wizard")
            .With("background", "sage")
            .With("abilities", new AbilityScoreChoice
            {
                Method = "standardArray",
                Scores = new() { ["Strength"] = 8, ["Dexterity"] = 13, ["Constitution"] = 14, ["Intelligence"] = 15, ["Wisdom"] = 12, ["Charisma"] = 10 },
            })
            .With("skills", new[] { "Insight", "Investigation" })
            .With("spells", new SpellChoice
            {
                Cantrips = ["fire_bolt", "light", "mage_hand"],
                Known = ["magic_missile", "shield", "sleep", "burning_hands", "detect_magic", "mage_armor"],
                Prepared = ["magic_missile", "shield", "sleep", "mage_armor"],
            });

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
        var selector = new RulesetModuleSelector(
        [
            new Dnd5eRulesetResolver(roll, races, classes, backgrounds, spells, creatures),
            new NarrativeRulesetResolver(roll),
        ]);
        var sources = new CreationSources(
            races, classes, backgrounds, new FeatDefinitionProvider(host, Asm), spells, creatures, new ProgressionDefinitionProvider(host, Asm));
        var recipes = new CreationRecipeProvider(host, Asm, null, pluginRoot is null ? [] : [pluginRoot]);
        IRecipeValidator[] validators =
        [
            new PickOneOptionValidator(), new PickNOptionValidator(), new PickNCountValidator(), new AbilityScoresMethodValidator(),
            new StandardArrayValidator(), new PointBuyValidator(), new RollRangeValidator(), new SpellsCountForLevelValidator(),
        ];
        return new CharacterCreationService(selector, recipes, sources, validators, new CharacterBootstrapOrchestrator(selector));
    }

    private static List<string> Keys(IEnumerable<CreationStep> steps) => [.. steps.Select(s => s.Key)];
}
