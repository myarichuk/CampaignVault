using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Phase 5 of the mechanics remediation: level-up and multiclass (M-40 to M-44).</summary>
[Collection("RavenDB")]
public class MechanicsPhase5Tests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private static readonly CampaignDocumentKeys Keys = new();

    // --- M-40: hit points for every level gained -------------------------------------------------------------------

    [Fact]
    public async Task M40_TwoLevelsGained_GiveTwoHitDice_AndMatchCreatingAtTheNewLevel()
    {
        var step = new Dnd5eDeriveHitPointsStep(new DefaultRollService(new Random(1)));
        var grown = Fighter(3, hp: 28, constitution: 14);
        await step.ApplyLevelGainAsync(
            new BootstrapContext { Character = grown, ActiveSystem = RulesetSystem.Dnd5e, LevelsGained = 2, ClassGained = "Fighter" },
            TestContext.Current.CancellationToken);

        var created = Fighter(5, hp: 0, constitution: 14);
        await step.ApplyAsync(
            new BootstrapContext { Character = created, ActiveSystem = RulesetSystem.Dnd5e },
            TestContext.Current.CancellationToken);

        Assert.Equal(created.MaxHp, grown.MaxHp);
        Assert.Equal(12 + 4 * 8, grown.MaxHp);
        Assert.Equal(5, ((Dnd5eExtension)grown.SystemStats!).Level);
    }

    [Fact]
    public async Task M40_RolledMode_RollsOncePerLevelGained()
    {
        var rolls = new CountingRollService();
        var step = new Dnd5eDeriveHitPointsStep(rolls);
        var hero = Fighter(3, hp: 28, constitution: 10);

        var result = await step.ApplyLevelGainAsync(
            new BootstrapContext
            {
                Character = hero, ActiveSystem = RulesetSystem.Dnd5e, LevelsGained = 2, ClassGained = "Fighter",
                HpModeOverride = HitPointDerivationMode.Rolled,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, rolls.Count);
        Assert.Equal(28 + 5 + 5, hero.MaxHp);
        Assert.Contains("+5, +5", result!.Message);
    }

    // --- M-42: current hit points ----------------------------------------------------------------------------------

    [Fact]
    public void M42_HealToMatch_DefaultsToTrue_AndAnExplicitFalseOptsOut()
    {
        Assert.True(new LevelUpChange().HealToMatch);
        Assert.True(((LevelUpChange)JsonSerializer.Deserialize<WorldChange>("""{"$type":"level_up","characterId":"chars/x"}""")!).HealToMatch);
        Assert.False(((LevelUpChange)JsonSerializer.Deserialize<WorldChange>("""{"$type":"level_up","characterId":"chars/x","healToMatch":false}""")!).HealToMatch);
    }

    [Fact]
    public async Task M42_LevelUp_RaisesCurrentHp_UnlessOptedOut()
    {
        var (result, hero, _) = await LevelUpAsync("m42-default", Fighter(5, 44, constitution: 14, current: 30), new LevelUpChange { LevelsGained = 1 });
        Assert.True(result.Success);
        Assert.Equal(52, hero.MaxHp);
        Assert.Equal(38, hero.CurrentHp);

        var (_, kept, _) = await LevelUpAsync("m42-optout", Fighter(5, 44, constitution: 14, current: 30), new LevelUpChange { LevelsGained = 1, HealToMatch = false });
        Assert.Equal(52, kept.MaxHp);
        Assert.Equal(30, kept.CurrentHp);
    }

    [Fact]
    public async Task LevelUp_ReportsTheCurrentHpMove_SoTheClientNeedsNoPartyRefresh()
    {
        var (_, _, messages) = await LevelUpAsync("hp-note", Fighter(5, 44, constitution: 14, current: 30), new LevelUpChange { LevelsGained = 1 });
        Assert.Contains(messages, m => m.Contains("MaxHp 44 → 52") && m.Contains("Current HP 30 → 38"));
    }

    [Fact]
    public async Task LevelUp_AtZeroHp_RaisesMaxOnly_AndSaysWhyCurrentStayed()
    {
        var (_, hero, messages) = await LevelUpAsync("hp-downed", Fighter(5, 44, constitution: 14, current: 0), new LevelUpChange { LevelsGained = 1 });
        Assert.Equal(52, hero.MaxHp);
        Assert.Equal(0, hero.CurrentHp);
        Assert.Contains(messages, m => m.Contains("Current HP unchanged (at 0 HP"));
    }

    [Fact]
    public async Task LevelUp_HealToMatch_RespectsTheExhaustionCapOnMaxHp()
    {
        // Exhaustion 4 halves the hit point maximum; the level's new hit points cannot lift current above that cap,
        // exactly as a heal through HpChangeHandler cannot.
        var hero = Fighter(5, 44, constitution: 14, current: 22);
        hero.SystemStats!.StatusEffects.Add(new StatusEffect { Name = "Exhaustion 4", Category = "Condition", ConditionName = "exhaustion" });

        var (_, after, _) = await LevelUpAsync("hp-exhausted", hero, new LevelUpChange { LevelsGained = 1 });

        Assert.Equal(52, after.MaxHp);
        Assert.Equal(26, after.CurrentHp); // 22 + 8 = 30, capped at 52 / 2
    }

    // --- M-43: retroactive Constitution ------------------------------------------------------------------------------

    [Fact]
    public async Task M43_ConstitutionIncrease_AddsTheModifierRiseForEveryEarlierLevel()
    {
        var (result, hero, messages) = await LevelUpAsync(
            "m43-up", Fighter(3, 28, constitution: 14),
            new LevelUpChange { LevelsGained = 1, AbilityScoreIncreases = new() { ["Constitution"] = 2 } });

        Assert.True(result.Success);
        // Created at level 4 with Constitution 16: 13 + 3 * 9.
        Assert.Equal(40, hero.MaxHp);
        Assert.Contains(messages, m => m.Contains("Constitution modifier +2 → +3") && m.Contains("+3 max HP"));
    }

    [Fact]
    public async Task M43_ConstitutionDrop_LowersMaxHp_AndClampsCurrentHp()
    {
        var (_, hero, _) = await LevelUpAsync(
            "m43-down", Fighter(3, 28, constitution: 14),
            new LevelUpChange { LevelsGained = 1, AbilityScoreIncreases = new() { ["Constitution"] = -2 }, HealToMatch = false });

        // Level 4 at modifier +1: 11 + 3 * 7 = 32.
        Assert.Equal(32, hero.MaxHp);
        Assert.Equal(28, hero.CurrentHp);
        Assert.True(hero.CurrentHp <= hero.MaxHp);
    }

    [Fact]
    public async Task M43_AStatedStatBlockMaximum_IsNotRecomputed()
    {
        var goblin = Fighter(3, 21, constitution: 14);
        ((Dnd5eExtension)goblin.SystemStats!).StatBlockHp = 21;
        var (_, after, _) = await LevelUpAsync(
            "m43-statblock", goblin, new LevelUpChange { LevelsGained = 1, AbilityScoreIncreases = new() { ["Constitution"] = 2 } });

        Assert.Equal(21, after.MaxHp);
    }

    // --- M-44: armor class -------------------------------------------------------------------------------------------

    [Fact]
    public async Task M44_ADexterityIncrease_RaisesArmorClass()
    {
        var hero = Fighter(3, 28, constitution: 14);
        var stats = (Dnd5eExtension)hero.SystemStats!;
        stats.Dexterity = 14;
        stats.ArmorClass = 12;

        var (_, after, _) = await LevelUpAsync("m44-dex", hero, new LevelUpChange { LevelsGained = 1, AbilityScoreIncreases = new() { ["Dexterity"] = 2 } });

        Assert.Equal(13, ((Dnd5eExtension)after.SystemStats!).ArmorClass);
    }

    [Fact]
    public async Task M44_AStatedArmorClass_MovesByTheSameAmount_NotToTheFormula()
    {
        var hero = Fighter(3, 28, constitution: 14);
        var stats = (Dnd5eExtension)hero.SystemStats!;
        stats.Dexterity = 14;
        stats.ArmorClass = 17; // a stat block's own figure: the formula alone would say 12

        var (_, after, _) = await LevelUpAsync("m44-stated", hero, new LevelUpChange { LevelsGained = 1, AbilityScoreIncreases = new() { ["Dexterity"] = 2 } });

        Assert.Equal(18, ((Dnd5eExtension)after.SystemStats!).ArmorClass);
    }

    [Fact]
    public async Task M44_AnUnrelatedLevelUp_LeavesArmorClassAlone()
    {
        var hero = Fighter(3, 28, constitution: 14);
        ((Dnd5eExtension)hero.SystemStats!).ArmorClass = 16;

        var (_, after, _) = await LevelUpAsync("m44-none", hero, new LevelUpChange { LevelsGained = 1 });

        Assert.Equal(16, ((Dnd5eExtension)after.SystemStats!).ArmorClass);
    }

    [Fact]
    public async Task M44_Pf2eArmorClass_RisesWithTheLevel()
    {
        var hero = new Character
        {
            Id = "chars/m44-pf2e", Name = "Pip", IsPc = true, ClassLevel = "Fighter 3", MaxHp = 38, CurrentHp = 38,
            SystemStats = new Pf2eExtension
            {
                Level = 3, ClassHpPerLevel = 10, AncestryHp = 8, ConstitutionMod = 2, DexterityMod = 2,
                AcProficiency = Pf2eProficiencyRank.Trained, ArmorClass = 17,
            },
        };

        var (result, after, _) = await LevelUpAsync("m44-pf2e", hero, new LevelUpChange { LevelsGained = 1 }, RulesetSystem.Pathfinder2e);

        Assert.True(result.Success);
        Assert.Equal(18, ((Pf2eExtension)after.SystemStats!).ArmorClass);
    }

    // --- M-41: a new class on level-up -------------------------------------------------------------------------------

    [Fact]
    public async Task M41_Fighter5TakingWizard_BecomesFighter5Wizard1()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        var stats = (Dnd5eExtension)hero.SystemStats!;
        stats.ClassLevels = [new ClassLevelEntry { Class = "Fighter", Level = 5 }];
        stats.Intelligence = 14;

        var (result, after, _) = await LevelUpAsync("m41-struct", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Wizard" });

        Assert.True(result.Success, string.Join("; ", result.Message ?? ""));
        var afterStats = (Dnd5eExtension)after.SystemStats!;
        Assert.Equal("Fighter 5 / Wizard 1", after.ClassLevel);
        Assert.Equal([("Fighter", 5), ("Wizard", 1)], afterStats.ClassLevels.Select(e => (e.Class, e.Level)));
        Assert.Equal(6, afterStats.Level);
        // The first Wizard level uses a d6 (average 4) and the Constitution modifier (+2), not the fighter's d10.
        Assert.Equal(44 + 4 + 2, after.MaxHp);
        // The steps after the hit points saw the new class: a Wizard casts with Intelligence.
        Assert.Equal("Intelligence", afterStats.SpellcastingAbility);
    }

    [Fact]
    public async Task M41_ACharacterKeptAsAString_GetsAnEntryForTheNewClass_NotAHigherFighterLevel()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        ((Dnd5eExtension)hero.SystemStats!).Intelligence = 14;
        hero.ClassLevel = "Fighter 5";

        var (result, after, _) = await LevelUpAsync("m41-string", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "wizard" });

        Assert.True(result.Success);
        Assert.Equal("Fighter 5 / Wizard 1", after.ClassLevel);
        Assert.Equal(2, ((Dnd5eExtension)after.SystemStats!).ClassLevels.Count);
    }

    [Fact]
    public async Task M41_AMissedRequirement_BlocksAndNamesTheAbilityAndScore_ChangingNothing()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        ((Dnd5eExtension)hero.SystemStats!).Intelligence = 10;

        var (result, after, _) = await LevelUpAsync("m41-blocked", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Wizard" });

        Assert.False(result.Success);
        Assert.Contains("Intelligence 13", result.Message);
        Assert.Contains("Intelligence 10", result.Message);
        Assert.Contains("overridePrerequisites", result.Message);
        Assert.Equal("Fighter 5", after.ClassLevel);
        Assert.Equal(44, after.MaxHp);
        Assert.Equal(5, ((Dnd5eExtension)after.SystemStats!).Level);
    }

    [Fact]
    public async Task M41_TheCurrentClassesRequirementCounts_Too()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        var stats = (Dnd5eExtension)hero.SystemStats!;
        stats.Intelligence = 14;
        stats.Strength = 10;
        stats.Dexterity = 10;

        var (result, _, _) = await LevelUpAsync("m41-leaving", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Wizard" });

        Assert.False(result.Success);
        Assert.Contains("leaving Fighter needs Strength or Dexterity 13", result.Message);
    }

    [Fact]
    public async Task M41_TheDmOverride_TakesTheClassAndRecordsWhatWasUnmet()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        ((Dnd5eExtension)hero.SystemStats!).Intelligence = 10;

        var (result, after, messages) = await LevelUpAsync(
            "m41-override", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Wizard", OverridePrerequisites = true });

        Assert.True(result.Success);
        Assert.Equal("Fighter 5 / Wizard 1", after.ClassLevel);
        Assert.Contains(messages, m => m.StartsWith("DM override:") && m.Contains("Intelligence 13"));
    }

    [Fact]
    public async Task M41_ACharacterThatAlreadyHasTheClass_NeedsNoRequirements()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        var stats = (Dnd5eExtension)hero.SystemStats!;
        stats.Strength = 8;
        stats.Dexterity = 8;
        stats.ClassLevels = [new ClassLevelEntry { Class = "Fighter", Level = 5 }];
        hero.ClassLevel = "Fighter 5";

        var (result, after, _) = await LevelUpAsync("m41-existing", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Fighter" });

        Assert.True(result.Success);
        Assert.Equal("Fighter 6", after.ClassLevel);
    }

    [Theory]
    [InlineData("War")]
    [InlineData("Artificer")]
    [InlineData("Warlocks")]
    public async Task M41_ANameThatIsNotAClass_IsRefused_NotMatchedByPrefix(string name)
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        ((Dnd5eExtension)hero.SystemStats!).Charisma = 16;

        var (result, after, _) = await LevelUpAsync("m41-unknown-" + name.ToLowerInvariant(), hero, new LevelUpChange { LevelsGained = 1, ClassGained = name });

        Assert.False(result.Success);
        Assert.Contains("not a class", result.Message);
        Assert.Equal("Fighter 5", after.ClassLevel);
    }

    [Fact]
    public async Task M41_AMulticlassCharacter_MustNameTheClassGainingTheLevel()
    {
        var hero = Fighter(5, 44, constitution: 14, current: 44);
        hero.ClassLevel = "Fighter 5 / Wizard 2";
        ((Dnd5eExtension)hero.SystemStats!).ClassLevels =
            [new ClassLevelEntry { Class = "Fighter", Level = 5 }, new ClassLevelEntry { Class = "Wizard", Level = 2 }];

        var (result, _, _) = await LevelUpAsync("m41-ambiguous", hero, new LevelUpChange { LevelsGained = 1 });

        Assert.False(result.Success);
        Assert.Contains("classGained", result.Message);
    }

    [Fact]
    public async Task M41_ASingleClassCharacter_StillLevelsWithoutNamingTheClass()
    {
        var (result, after, _) = await LevelUpAsync("m41-single", Fighter(5, 44, constitution: 14, current: 44), new LevelUpChange { LevelsGained = 1 });

        Assert.True(result.Success);
        Assert.Equal("Fighter 6", after.ClassLevel);
    }

    [Fact]
    public async Task M41_Pathfinder_RefusesAClassTheCharacterLacks()
    {
        var hero = new Character
        {
            Id = "chars/m41-pf2e", Name = "Pip", IsPc = true, ClassLevel = "Fighter 3", MaxHp = 38, CurrentHp = 38,
            SystemStats = new Pf2eExtension { Level = 3, ClassHpPerLevel = 10, AncestryHp = 8, ConstitutionMod = 2 },
        };

        var (result, after, _) = await LevelUpAsync("m41-pf2e", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Wizard" }, RulesetSystem.Pathfinder2e);

        Assert.False(result.Success);
        Assert.Contains("archetype", result.Message);
        Assert.Equal(38, after.MaxHp);
    }

    [Fact]
    public void M41_ThePlanner_PlansTheNewClassesLevelOne_AndIgnoresTheOtherClassesPicks()
    {
        var planner = CampaignVault.UnitTests.LevelUpPlannerTests.Create(Path.Combine(Path.GetTempPath(), "cv-p5-" + Guid.NewGuid().ToString("N")));
        var hero = Fighter(5, 44, constitution: 14);
        ((Dnd5eExtension)hero.SystemStats!).LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "archery" });

        var plan = planner.Plan(hero, RulesetSystem.Dnd5e, "Wizard")!;

        Assert.Equal("Wizard", plan.ClassName, ignoreCase: true);
        Assert.Equal(1, plan.ClassLevel);
        Assert.Equal(6, plan.CharacterLevel);
        Assert.True(plan.IsNewClass);
        Assert.DoesNotContain(plan.Slots, s => s.Id.Contains("fightingStyle", StringComparison.OrdinalIgnoreCase));
        Assert.Null(planner.Plan(hero, RulesetSystem.Dnd5e, "War"));
    }

    [Fact]
    public void M41_ClassLevelSync_AddsTheNewClass_AndLeavesTheOldOnesAlone()
    {
        var hero = Fighter(5, 44, constitution: 14);
        ((Dnd5eExtension)hero.SystemStats!).Level = 6; // the hit point step has already counted the level

        BootstrapClassLevelHelper.SyncClassLevelFromStats(hero, "Wizard");

        Assert.Equal("Fighter 5 / Wizard 1", hero.ClassLevel);

        BootstrapClassLevelHelper.SyncClassLevelFromStats(hero, "wizard", 2);
        Assert.Equal("Fighter 5 / Wizard 3", hero.ClassLevel);
        Assert.Equal(8, ((Dnd5eExtension)hero.SystemStats).Level);
    }


    [Fact]
    public async Task M41_AClassWrittenWithoutANumber_StillGetsStructuredEntries_WhenAClassIsAdded()
    {
        var hero = new Character
        {
            Id = "x", Name = "Mira", IsPc = true, ClassLevel = "Human Wizard", MaxHp = 17, CurrentHp = 17,
            SystemStats = new Dnd5eExtension { Level = 3, HitDie = "d6", Constitution = 10, Strength = 14, Intelligence = 15 },
        };

        var (result, after, messages) = await LevelUpAsync("m41-nonumber", hero, new LevelUpChange { LevelsGained = 1, ClassGained = "Fighter" });

        Assert.True(result.Success);
        var stats = (Dnd5eExtension)after.SystemStats!;
        Assert.Equal(4, stats.Level);
        Assert.Equal([3, 1], stats.ClassLevels.Select(e => e.Level));
        Assert.Contains("Fighter 1", after.ClassLevel);
        Assert.Equal(17 + 6, after.MaxHp); // a d10 (average 6) and no Constitution bonus
        Assert.Contains(messages, m => m.Contains("takes a first level in Fighter") && m.Contains("Fighter 1"));
    }

    [Fact]
    public async Task M42_ADeadOrDyingPc_IsNotHealedByALevelUp()
    {
        var dead = Fighter(5, 44, constitution: 14, current: 0);
        dead.Death = new DeathRecord();
        var (_, deadAfter, _) = await LevelUpAsync("m42-dead", dead, new LevelUpChange { LevelsGained = 1 });
        Assert.Equal(0, deadAfter.CurrentHp);

        var dying = Fighter(5, 44, constitution: 14, current: 0);
        var (_, dyingAfter, _) = await LevelUpAsync("m42-dying", dying, new LevelUpChange { LevelsGained = 1 });
        Assert.Equal(52, dyingAfter.MaxHp);
        Assert.Equal(0, dyingAfter.CurrentHp);
    }

    [Fact]
    public async Task M41_TheMenu_AsksAMulticlassCharacterWhichClass_AndShowsTheNewClassesFeatures()
    {
        var (slug, id) = await StoreAsync("m41-menu", Fighter(5, 44, constitution: 14));
        var tool = TestCampaignToolsFactory.CreateTool<CharacterLevelUpTools>(fixture);

        var single = await tool.CharacterLevelUp("options", id, slug, classGained: "Wizard");
        Assert.True(single.Success, single.Summary);
        Assert.Equal("Wizard", single.Data!.ClassName, ignoreCase: true);
        Assert.DoesNotContain(single.Data.Features, f => f.Name.Contains("Second Wind", StringComparison.OrdinalIgnoreCase));

        var (multi, mid) = await StoreAsync("m41-menu-multi", Fighter(5, 44, constitution: 14), "Fighter 5 / Wizard 2");
        var ambiguous = await tool.CharacterLevelUp("options", mid, multi);
        Assert.False(ambiguous.Success);
        Assert.Contains("classGained", ambiguous.Summary);
        Assert.Contains("Wizard", ambiguous.Summary);

        var named = await tool.CharacterLevelUp("options", mid, multi, classGained: "Wizard");
        Assert.True(named.Success, named.Summary);
        Assert.Equal("Wizard", named.Data!.ClassName, ignoreCase: true);
    }

    [Fact]
    public async Task M41_TheLookup_TakesTheClassGaining_AndRefusesAMulticlassCharacterThatDoesntSay()
    {
        var (slug, id) = await StoreAsync("m41-lookup", Fighter(5, 44, constitution: 14));
        var tool = TestCampaignToolsFactory.CreateTool<CampaignManagementTools>(fixture);

        var wizard = await tool.GetPendingLevelUpChoices(id, slug, "Wizard");
        Assert.True(wizard.Success, wizard.Summary);
        Assert.Equal("Wizard", wizard.Data!.ClassName, ignoreCase: true);
        Assert.Equal(6, wizard.Data.TargetLevel);

        var (multi, mid) = await StoreAsync("m41-lookup-multi", Fighter(5, 44, constitution: 14), "Fighter 5 / Wizard 2");
        var ambiguous = await tool.GetPendingLevelUpChoices(mid, multi);
        Assert.False(ambiguous.Success);
        Assert.Contains("className", ambiguous.Summary);
    }

    // --- helpers ---------------------------------------------------------------------------------------------------

    private static Character Fighter(int level, int hp, int constitution, int? current = null) => new()
    {
        Id = "chars/hero",
        Name = "Hild",
        IsPc = true,
        ClassLevel = $"Fighter {level}",
        MaxHp = hp,
        CurrentHp = current ?? hp,
        SystemStats = new Dnd5eExtension { Level = level, HitDie = "d10", Constitution = constitution, Strength = 16, Dexterity = 12 },
    };

    private async Task<(string Slug, string Id)> StoreAsync(string campaign, Character character, string? classLevel = null)
    {
        using var session = fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = Keys.Config(campaign), ActiveSystem = RulesetSystem.Dnd5e }, Keys.Config(campaign), TestContext.Current.CancellationToken);
        character.Id = $"chars/{campaign}";
        character.CampaignName = campaign;
        if (classLevel is not null)
        {
            character.ClassLevel = classLevel;
            var stats = (Dnd5eExtension)character.SystemStats!;
            stats.ClassLevels = [.. Dnd5eClassProfileResolverShim.Parse(classLevel)];
            stats.Level = stats.ClassLevels.Sum(e => e.Level);
            stats.Intelligence = 14;
        }

        await session.StoreAsync(character, character.Id, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (campaign, character.Id);
    }

    private async Task<(ChangeHandlerResult Result, Character Character, List<string> Messages)> LevelUpAsync(
        string campaign, Character character, LevelUpChange change, string system = RulesetSystem.Dnd5e)
    {
        using var session = fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = Keys.Config(campaign), ActiveSystem = system }, TestContext.Current.CancellationToken);
        character.Id = $"chars/{campaign}";
        character.CampaignName = campaign;
        await session.StoreAsync(character, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var planner = CampaignVault.UnitTests.LevelUpPlannerTests.Create(Path.Combine(Path.GetTempPath(), "cv-p5-" + Guid.NewGuid().ToString("N")));
        var (_, classes, conditions, _, initializer) = RulesetDataTestHelper.CreateServices();
        var handler = new LevelUpChangeHandler(Keys, BootstrapTestHelper.CreateOrchestrator(), initializer, planner, classes, conditions: conditions);
        var messages = new List<string>();
        var ctx = CreateContext(session, campaign, messages);
        ctx.RegisterNewCharacter(character);

        change.CharacterId = character.Id;
        var result = await handler.ApplyAsync(change, ctx, TestContext.Current.CancellationToken);
        return (result, character, result.Success ? messages : [.. messages, result.Message ?? ""]);
    }

    private static ChangeContext CreateContext(IAsyncDocumentSession session, string campaign, List<string> summary) =>
        new(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            summary,
            new WorldChangeDispatcher([], Keys, NullLogger<WorldChangeDispatcher>.Instance),
            null,
            null,
            campaign);

    private static class Dnd5eClassProfileResolverShim
    {
        public static IEnumerable<ClassLevelEntry> Parse(string text) =>
            text.Split('/', StringSplitOptions.TrimEntries).Select(part => part.Split(' ')).Select(p => new ClassLevelEntry { Class = p[0], Level = int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) });
    }

    private sealed class CountingRollService : IRollService
    {
        public int Count { get; private set; }

        public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default)
        {
            Count++;
            return Task.FromResult(new RollOutcome { Tag = request.Tag, Result = 5 + request.Bonus });
        }

        public async Task<IReadOnlyList<RollOutcome>> RollBatchAsync(IEnumerable<RollRequest> requests, CancellationToken ct = default)
        {
            var results = new List<RollOutcome>();
            foreach (var request in requests)
            {
                results.Add(await RollAsync(request, ct));
            }

            return results;
        }
    }
}
