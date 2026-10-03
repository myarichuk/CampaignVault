using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class CharacterWiringAuditorTests
{
    private static (CharacterWiringAuditor Auditor, CampaignVault.Services.ResourcePoolInitializer Initializer) Create()
    {
        var (_, classes, _, feats, initializer) = RulesetDataTestHelper.CreateServices();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_wiring_test_" + System.Guid.NewGuid());
        var progressions = new CampaignVault.Services.ProgressionDefinitionProvider(dir, typeof(CampaignVault.Services.ResourcePoolProvider).Assembly);
        return (new CharacterWiringAuditor(initializer, classes, feats, progressions: progressions), initializer);
    }

    private static Character FighterRogue(Dnd5eExtension? stats = null) => new()
    {
        Id = "chars/hank",
        Name = "Hank",
        IsPc = true,
        KeepAlive = true,
        MaxHp = 50,
        ClassLevel = "Fighter 4 / Scout Rogue 3",
        SystemStats = stats ?? new Dnd5eExtension
        {
            Level = 7,
            Dexterity = 18,
            SkillModifiers = { ["Stealth"] = 7 },
            Attributes = { ["proficiencyBonus"] = 3 },
        },
    };

    [Fact]
    public void Dnd5e_ClassDeclaredButPoolsNeverInitialized_FlagsMissingActionSurge()
    {
        var (auditor, _) = Create();
        var findings = auditor.Audit(FighterRogue(), RulesetSystem.Dnd5e, null);

        var missing = Assert.Single(findings, f => f.Code == "missing_pools");
        Assert.Contains("action_surge", missing.Message);
    }

    [Fact]
    public void Dnd5e_AfterInitializePools_NoMissingPools()
    {
        var (auditor, initializer) = Create();
        var hank = FighterRogue();
        initializer.InitializePools(hank, RulesetSystem.Dnd5e, null);

        Assert.DoesNotContain(auditor.Audit(hank, RulesetSystem.Dnd5e, null), f => f.Code is "missing_pools" or "stale_pools");
    }

    [Fact]
    public void Dnd5e_AuditDoesNotMutateStoredPools()
    {
        var (auditor, _) = Create();
        var hank = FighterRogue();
        auditor.Audit(hank, RulesetSystem.Dnd5e, null);

        Assert.Empty(hank.SystemStats!.ResourcePools);
    }

    [Fact]
    public void Dnd5e_StalePoolMax_IsFlagged()
    {
        var (auditor, initializer) = Create();
        var hank = FighterRogue();
        initializer.InitializePools(hank, RulesetSystem.Dnd5e, null);
        hank.SystemStats!.ResourcePools["action_surge"] = hank.SystemStats.ResourcePools["action_surge"] with { Max = 5 };

        Assert.Contains(auditor.Audit(hank, RulesetSystem.Dnd5e, null), f => f.Code == "stale_pools");
    }

    [Fact]
    public void Dnd5e_UnknownClassName_IsFlagged()
    {
        var (auditor, _) = Create();
        var pc = FighterRogue();
        pc.ClassLevel = "Bladesinger 5";
        ((Dnd5eExtension)pc.SystemStats!).Level = 5;

        Assert.Contains(auditor.Audit(pc, RulesetSystem.Dnd5e, null), f => f.Code == "unresolved_class");
    }

    [Fact]
    public void Dnd5e_LevelDisagreesWithClasses_IsFlagged()
    {
        var (auditor, _) = Create();
        var pc = FighterRogue();
        ((Dnd5eExtension)pc.SystemStats!).Level = 5;

        Assert.Contains(auditor.Audit(pc, RulesetSystem.Dnd5e, null), f => f.Code == "level_mismatch");
    }

    [Fact]
    public void Dnd5e_StaleOrMissingProficiencyBonus_IsFlagged()
    {
        var (auditor, _) = Create();
        var pc = FighterRogue();
        ((Dnd5eExtension)pc.SystemStats!).Attributes["proficiencyBonus"] = 2;

        Assert.Contains(auditor.Audit(pc, RulesetSystem.Dnd5e, null), f => f.Code == "proficiency_stale");
    }

    [Fact]
    public void Dnd5e_NoClassSkillsCommitted_IsFlagged()
    {
        var (auditor, _) = Create();
        var pc = FighterRogue();
        ((Dnd5eExtension)pc.SystemStats!).SkillModifiers.Clear();

        Assert.Contains(auditor.Audit(pc, RulesetSystem.Dnd5e, null), f => f.Code == "class_skills_unset");
    }

    [Fact]
    public void Dnd5e_CasterWithoutSpellcastingStats_IsFlagged()
    {
        var (auditor, initializer) = Create();
        var wizard = new Character
        {
            Id = "chars/wiz",
            Name = "Wiz",
            KeepAlive = true,
            ClassLevel = "Wizard 3",
            SystemStats = new Dnd5eExtension
            {
                Level = 3,
                SkillModifiers = { ["Arcana"] = 5 },
                Attributes = { ["proficiencyBonus"] = 2 },
            },
        };
        initializer.InitializePools(wizard, RulesetSystem.Dnd5e, null);

        Assert.Contains(auditor.Audit(wizard, RulesetSystem.Dnd5e, null), f => f.Code == "caster_unwired");
    }

    [Fact]
    public void Dnd5e_UnknownFeat_IsFlagged()
    {
        var (auditor, initializer) = Create();
        var pc = FighterRogue();
        ((Dnd5eExtension)pc.SystemStats!).Feats.Add("definitely_not_a_feat");
        initializer.InitializePools(pc, RulesetSystem.Dnd5e, null);

        var finding = Assert.Single(auditor.Audit(pc, RulesetSystem.Dnd5e, null), f => f.Code == "unresolved_references");
        Assert.Contains("definitely_not_a_feat", finding.Message);
    }

    [Fact]
    public void Dnd5e_FighterPcWithoutRecordedFightingStyle_IsFlagged()
    {
        var (auditor, _) = Create();
        var hank = FighterRogue();

        var finding = Assert.Single(auditor.Audit(hank, RulesetSystem.Dnd5e, null), f => f.Code == "missing_choices");
        Assert.Contains("fightingStyle", finding.Message);

        ((Dnd5eExtension)hank.SystemStats!).LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "archery" });
        Assert.DoesNotContain(auditor.Audit(hank, RulesetSystem.Dnd5e, null), f => f.Message.Contains("fightingStyle") && f.Code == "missing_choices");
    }

    [Fact]
    public void Dnd5e_MulticlassSubclassPicks_WrittenWithTheClassInTheKey_CountAsRecorded()
    {
        var (auditor, _) = Create();
        var maeve = FighterRogue();
        maeve.ClassLevel = "Sorcerer 2 / Wizard 10";
        var stats = (Dnd5eExtension)maeve.SystemStats!;
        stats.Level = 12;

        var before = auditor.Audit(maeve, RulesetSystem.Dnd5e, null).Single(f => f.Code == "missing_choices");
        Assert.Contains("Sorcerer subclass", before.Message);
        Assert.Contains("Wizard subclass", before.Message);
        Assert.Contains("\"class\"", before.Message); // the nag shows the shape to write

        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "Sorcerer subclass", Value = "Storm Sorcery" });
        stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 2, Key = "Wizard subclass", Value = "Evocation" });
        Assert.DoesNotContain(auditor.Audit(maeve, RulesetSystem.Dnd5e, null), f => f.Code == "missing_choices");
    }

    [Fact]
    public void EmptyPurse_IsFlaggedForThePcOnly()
    {
        var (auditor, initializer) = Create();
        var pc = FighterRogue();
        initializer.InitializePools(pc, RulesetSystem.Dnd5e, null);
        Assert.Contains(auditor.Audit(pc, RulesetSystem.Dnd5e, null), f => f.Code == "purse_empty");

        var npc = FighterRogue();
        npc.IsPc = false;
        initializer.InitializePools(npc, RulesetSystem.Dnd5e, null);
        Assert.DoesNotContain(auditor.Audit(npc, RulesetSystem.Dnd5e, null), f => f.Code == "purse_empty");
    }

    [Fact]
    public void Dnd5e_MissingClassSaves_AreFlagged()
    {
        var (auditor, _) = Create();
        var hank = FighterRogue();

        Assert.Contains(auditor.Audit(hank, RulesetSystem.Dnd5e, null), f => f.Code == "class_saves_unset");
    }

    private static Dictionary<string, FeatEffectRules.CatalogFeat> Catalog(FeatEffectRules.CatalogFeat feat) =>
        new() { [CombatFeatureRules.Norm(feat.Name)] = feat };

    private (Character Hank, CharacterWiringAuditor Auditor) HankWithFeat(string feat)
    {
        var (auditor, initializer) = Create();
        var hank = FighterRogue();
        ((Dnd5eExtension)hank.SystemStats!).Feats.Add(feat);
        initializer.InitializePools(hank, RulesetSystem.Dnd5e, null);
        return (hank, auditor);
    }

    [Fact]
    public void Dnd5e_FeatNotInRulesetData_IsReportedUnresolved()
    {
        var (hank, auditor) = HankWithFeat("Not A Real Feat");

        Assert.Contains(auditor.Audit(hank, RulesetSystem.Dnd5e, null), f => f.Code == "unresolved_references");
    }

    [Fact]
    public void HomebrewFeat_InCatalog_IsResolved_ButFlaggedWhenItDeclaresNothing()
    {
        var (hank, auditor) = HankWithFeat("Long Shot");
        var bare = new FeatEffectRules.CatalogFeat("Long Shot", [], null, false, false, Homebrew: true);

        var findings = auditor.Audit(hank, RulesetSystem.Dnd5e, null, Catalog(bare));

        Assert.DoesNotContain(findings, f => f.Code == "unresolved_references");
        Assert.Contains(findings, f => f.Code == "feat_unimplemented_effects");
    }

    [Fact]
    public void HomebrewFeat_WithEffectsOrAdjudicated_IsNotFlagged()
    {
        var (hank, auditor) = HankWithFeat("Long Shot");
        var effect = new FeatEffect { Kind = FeatEffectKinds.AttackBonus, Value = 1 };
        var withEffects = new FeatEffectRules.CatalogFeat("Long Shot", [effect], null, false, false, Homebrew: true);
        var adjudicated = new FeatEffectRules.CatalogFeat("Long Shot", [], null, true, false, Homebrew: true);

        Assert.DoesNotContain(auditor.Audit(hank, RulesetSystem.Dnd5e, null, Catalog(withEffects)), f => f.Code == "feat_unimplemented_effects");
        Assert.DoesNotContain(auditor.Audit(hank, RulesetSystem.Dnd5e, null, Catalog(adjudicated)), f => f.Code == "feat_unimplemented_effects");
    }

    [Fact]
    public void Feat_GatedOnUnloadedPlugin_IsReportedInert()
    {
        var (hank, auditor) = HankWithFeat("Astral Focus");
        var gated = new FeatEffectRules.CatalogFeat("Astral Focus", [], new FeatRequirement { Plugin = "no-such-plugin-" + System.Guid.NewGuid() }, false, false, Homebrew: true);

        Assert.Contains(auditor.Audit(hank, RulesetSystem.Dnd5e, null, Catalog(gated)), f => f.Code == "feat_gated_off");
    }

    [Fact]
    public void StatBlockCreatureWithoutClass_IsNotFlagged()
    {
        var (auditor, _) = Create();
        var goblin = new Character
        {
            Id = "chars/goblin",
            Name = "Goblin",
            MaxHp = 7,
            SystemStats = new Dnd5eExtension { ArmorClass = 15, StatBlockHp = 7 },
        };

        Assert.Empty(auditor.Audit(goblin, RulesetSystem.Dnd5e, null));
    }

    [Fact]
    public void Pf2e_PlaceholderTrainedEverything_IsFlagged()
    {
        var (auditor, initializer) = Create();
        var skills = new[]
        {
            "Acrobatics", "Arcana", "Athletics", "Crafting", "Deception", "Diplomacy", "Intimidation", "Medicine",
            "Nature", "Occultism", "Performance", "Religion", "Society", "Stealth", "Survival", "Thievery", "Lore",
        };
        var stats = new Pf2eExtension { Level = 3 };
        foreach (var skill in skills)
        {
            stats.SkillProficiencies[skill] = Pf2eProficiencyRank.Trained;
        }

        var fighter = new Character { Id = "chars/pf", Name = "Pf", KeepAlive = true, IsPc = true, ClassLevel = "Human Fighter 3", SystemStats = stats };
        initializer.InitializePools(fighter, RulesetSystem.Pathfinder2e, null);

        Assert.Contains(auditor.Audit(fighter, RulesetSystem.Pathfinder2e, null), f => f.Code == "default_proficiencies");
    }

    [Fact]
    public void Pf2e_ClassStringWithoutLevel_FlagsUnparsedClass()
    {
        var (auditor, _) = Create();
        var wizard = new Character
        {
            Id = "chars/pfw",
            Name = "Pfw",
            KeepAlive = true,
            ClassLevel = "Human Wizard",
            SystemStats = new Pf2eExtension(),
        };

        Assert.Contains(auditor.Audit(wizard, RulesetSystem.Pathfinder2e, null), f => f.Code == "class_level_unparsed");
    }

    [Fact]
    public void Pf2e_ClassStringWithoutLevel_UsesStatsLevelForPools()
    {
        var (auditor, initializer) = Create();
        var wizard = new Character
        {
            Id = "chars/pfw",
            Name = "Pfw",
            KeepAlive = true,
            ClassLevel = "Human Wizard",
            SystemStats = new Pf2eExtension { Level = 3 },
        };

        initializer.InitializePools(wizard, RulesetSystem.Pathfinder2e, null);

        Assert.True(wizard.SystemStats!.ResourcePools.ContainsKey("focus_points"));
        Assert.DoesNotContain(auditor.Audit(wizard, RulesetSystem.Pathfinder2e, null), f => f.Code is "class_level_unparsed" or "missing_pools");
    }

    [Fact]
    public void Dnd5e_NpcWithClassButNoSkills_IsNotNaggedAboutClassSkills()
    {
        var (auditor, initializer) = Create();
        var npc = FighterRogue();
        npc.IsPc = false;
        npc.SystemStats = new Dnd5eExtension { Level = 7, Attributes = { ["proficiencyBonus"] = 3 } };
        initializer.InitializePools(npc, RulesetSystem.Dnd5e, null);

        Assert.DoesNotContain(auditor.Audit(npc, RulesetSystem.Dnd5e, null), f => f.Code == "class_skills_unset");
    }

    [Fact]
    public void ClassListingPoolMissingFromCampaignSchemas_IsFlagged()
    {
        var (auditor, _) = Create();
        var config = new CampaignConfig
        {
            ResourcePoolSchemas = { ["gold"] = new ResourcePoolTemplate { DefaultMax = 10 } },
        };

        var findings = auditor.Audit(FighterRogue(), RulesetSystem.Dnd5e, config);

        Assert.Contains(findings, f => f.Code == "class_pool_undefined" && f.Message.Contains("action_surge"));
    }

    [Fact]
    public void InitializePools_KeepsHandAddedPoolsUnknownToSchemas()
    {
        var (_, initializer) = Create();
        var hank = FighterRogue();
        hank.SystemStats!.ResourcePools["war_chest"] = new ResourcePool { Current = 3, Max = 5 };

        initializer.InitializePools(hank, RulesetSystem.Dnd5e, null);

        Assert.Equal(3, hank.SystemStats.ResourcePools["war_chest"].Current);
        Assert.True(hank.SystemStats.ResourcePools.ContainsKey("action_surge"));
    }

    private static ChangeContext CreateContext(params Character[] characters) => new(
        sessionForTests: null,
        characters: characters.ToDictionary(c => c.Id),
        items: new Dictionary<string, Item>(),
        locations: new Dictionary<string, Location>(),
        factions: new Dictionary<string, Faction>(),
        quests: new Dictionary<string, Quest>(),
        logger: NullLogger.Instance,
        summary: [],
        dispatcher: new WorldChangeDispatcher(
            new IWorldChangeHandler[0], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
        campaignName: null);
}
