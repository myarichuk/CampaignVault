using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CharacterBootstrapTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CharacterBootstrapTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dnd5e_DerivesAverageHp_WhenMaxHpOmitted()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/kergil",
            Name = "Kergil",
            ClassLevel = "Human Barbarian 10",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 16,
                HitDie = "d12",
                Level = 10,
            },
        };

        var report = await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        Assert.Equal(105, character.MaxHp);
        Assert.Equal(105, character.CurrentHp);
        Assert.Contains(report.Steps, s => s.StepName == "dnd5e.derive_hit_points");
    }

    [Fact]
    public async Task Dnd5e_ExplicitMaxHp_SkipsHpButStillDerivesDefenseAndProficiency()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/stat-block",
            Name = "Goblin Scout",
            ClassLevel = "Goblin 1",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 10,
                HitDie = "d6",
                Dexterity = 14,
            },
        };

        var report = await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
            ExplicitMaxHp = 7,
            ExplicitCurrentHp = 7,
        });

        Assert.Equal(7, character.MaxHp);
        Assert.Equal(7, character.CurrentHp);
        Assert.DoesNotContain(report.Steps, s => s.StepName == "dnd5e.derive_hit_points");
        Assert.Contains(report.Steps, s => s.StepName == "dnd5e.derive_defense");
        Assert.Contains(report.Steps, s => s.StepName == "dnd5e.derive_proficiency");
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
        Assert.Equal(12, stats.ArmorClass);
    }

    [Fact]
    public async Task Dnd5e_StatBlockHp_SkipsFormulaHpButDerivesDefense()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/goblin",
            Name = "Goblin",
            SystemStats = new Dnd5eExtension
            {
                StatBlockHp = 7,
                Dexterity = 14,
            },
        };

        await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        Assert.Equal(7, character.MaxHp);
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
        Assert.Equal(12, stats.ArmorClass);
    }

    [Fact]
    public async Task Pf2e_DerivesHp_WhenBootstrapFieldsPresent()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/pf-fighter",
            Name = "Elara",
            ClassLevel = "Human Fighter 2",
            SystemStats = new Pf2eExtension
            {
                ClassHpPerLevel = 10,
                AncestryHp = 8,
                Level = 2,
                ConstitutionMod = 2,
            },
        };

        await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Pathfinder2e,
        });

        Assert.Equal(32, character.MaxHp);
        Assert.Equal(32, character.CurrentHp);
    }

    [Fact]
    public async Task Fallout_DerivesHp_FromEnduranceLuckAndLevel()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/vault-dweller",
            Name = "Vault Dweller",
            SystemStats = new Fallout2d20Extension
            {
                Endurance = 6,
                Luck = 5,
                Level = 3,
            },
        };

        await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Fallout2d20,
        });

        Assert.Equal(23, character.MaxHp);
        Assert.Equal(23, character.CurrentHp);
    }

    [Fact]
    public async Task LevelUp_GainsHp_AndRefreshesProficiency_ForDnd5eBarbarian()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/kergil",
            Name = "Kergil",
            MaxHp = 95,
            CurrentHp = 60,
            ClassLevel = "Human Barbarian 9",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 16,
                HitDie = "d12",
                Level = 9,
                Attributes = { ["proficiencyBonus"] = 3 },
            },
        };

        var report = await orchestrator.ApplyLevelGainAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
            LevelsGained = 1,
        });

        Assert.Equal(105, character.MaxHp);
        Assert.Equal(60, character.CurrentHp);
        Assert.Equal("Human Barbarian 10", character.ClassLevel);
        Assert.Contains(report.Steps, s => s.StepName == "dnd5e.derive_hit_points");
        Assert.Contains(report.Steps, s => s.StepName == "dnd5e.derive_proficiency");
        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
        Assert.Equal(10, stats.Level);
        Assert.Equal(4f, stats.Attributes["proficiencyBonus"]);
    }

    [Fact]
    public async Task CharacterCreate_BootstrapsHpAndDefense_WhenMaxHpOmitted()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("bootstrap-hp"),
            ActiveSystem = RulesetSystem.Dnd5e,
        });
        await session.SaveChangesAsync();

        var handler = new CharacterCreateHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var change = new CharacterCreate
        {
            CharacterId = "chars/bootstrap-fighter",
            Name = "Bootstrap Fighter",
            KeepAlive = true,
            ClassLevel = "Human Fighter 1",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 14,
                Dexterity = 14,
            },
        };

        var summary = new List<string>();
        var ctx = CreateContext(session, "bootstrap-hp", summary);
        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        await session.SaveChangesAsync();

        var character = await session.LoadAsync<Character>("chars/bootstrap-fighter");
        var stats = Assert.IsType<Dnd5eExtension>(character!.SystemStats);
        Assert.Equal(12, character.MaxHp);
        Assert.Equal(12, character.CurrentHp);
        Assert.Equal(12, stats.ArmorClass);
        Assert.True(stats.Attributes.ContainsKey("proficiencyBonus"));
        Assert.Contains(summary, m => m.Contains("[BOOTSTRAP HINT]") && m.Contains("item_create"));
    }

    [Fact]
    public async Task LevelUpChangeHandler_HealToMatch_IncreasesCurrentHp()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("level-up-heal"),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var existing = new Character
        {
            Id = "chars/level-up-pc",
            Name = "Level Up PC",
            IsPc = true,
            CampaignName = "level-up-heal",
            MaxHp = 12,
            CurrentHp = 8,
            ClassLevel = "Human Fighter 1",
            SystemStats = new Dnd5eExtension
            {
                Constitution = 14,
                HitDie = "d10",
                Level = 1,
            },
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new LevelUpChangeHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var change = new LevelUpChange
        {
            CharacterId = "chars/level-up-pc",
            LevelsGained = 1,
            HealToMatch = true,
        };

        var ctx = CreateContext(session, "level-up-heal", []);
        ctx.RegisterNewCharacter(existing);
        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        Assert.Equal(20, existing.MaxHp);
        Assert.Equal(16, existing.CurrentHp);
    }

    [Fact]
    public async Task LevelUpChangeHandler_Warns_WhenNoRulesetStepsApply()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("level-up-noop"),
            ActiveSystem = RulesetSystem.Narrative,
        });

        var existing = new Character
        {
            Id = "chars/narrative-npc",
            Name = "Oracle",
            IsPc = true,
            CampaignName = "level-up-noop",
            MaxHp = 10,
            SystemStats = new Dnd5eExtension(),
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new LevelUpChangeHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var summary = new List<string>();
        var ctx = CreateContext(session, "level-up-noop", summary);
        ctx.RegisterNewCharacter(existing);

        var result = await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/narrative-npc",
            LevelsGained = 1,
        }, ctx);

        Assert.True(result.Success);
        Assert.Contains(summary, m => m.Contains("level_up") && m.Contains("no ruleset changes"));
    }

    [Fact]
    public async Task CharacterUpdate_BootstrapsOnSystemStatsPatch()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("char-update-bootstrap"),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var existing = new Character
        {
            Id = "chars/updated",
            Name = "Updated PC",
            MaxHp = 0,
            CurrentHp = 0,
            ClassLevel = "Human Fighter 1",
            SystemStats = new Dnd5eExtension { Constitution = 14 },
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new CharacterUpdateHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var summary = new List<string>();
        var ctx = CreateContext(session, "char-update-bootstrap", summary);
        var result = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = "chars/updated",
            SystemStats = new Dnd5eExtension { Dexterity = 14 },
        }, ctx);

        Assert.True(result.Success);
        Assert.Equal(12, existing.MaxHp);
        var stats = Assert.IsType<Dnd5eExtension>(existing.SystemStats);
        Assert.Equal(12, stats.ArmorClass);
    }

    [Fact]
    public async Task Pf2e_DerivesDefense_WhenArmorClassDefault()
    {
        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var character = new Character
        {
            Id = "chars/pf-rogue",
            Name = "Rogue",
            SystemStats = new Pf2eExtension { DexterityMod = 4 },
        };

        var report = await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Pathfinder2e,
        });

        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);
        Assert.Equal(14, stats.ArmorClass);
        Assert.Contains(report.Steps, s => s.StepName == "pf2e.derive_defense");
    }

    [Fact]
    public async Task Fallout_ReDerivesDefense_WhenAgilityPatched()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("fallout-agi-patch"),
            ActiveSystem = RulesetSystem.Fallout2d20,
        });

        var existing = new Character
        {
            Id = "chars/wastelander",
            Name = "Wastelander",
            MaxHp = 11,
            CurrentHp = 11,
            SystemStats = new Fallout2d20Extension
            {
                Endurance = 5,
                Luck = 6,
                Agility = 9,
                Defense = 2,
            },
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new SystemStatsChangeHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var ctx = CreateContext(session, "fallout-agi-patch", []);
        var result = await handler.ApplyAsync(new SystemStatsChange
        {
            CharacterId = "chars/wastelander",
            SystemStats = new Fallout2d20Extension { Agility = 7 },
        }, ctx);

        Assert.True(result.Success);
        var stats = Assert.IsType<Fallout2d20Extension>(existing.SystemStats);
        Assert.Equal(1, stats.Defense);
    }

    [Fact]
    public async Task LevelUpChangeHandler_Warns_WhenStatBlockHpSet()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("level-up-statblock"),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var existing = new Character
        {
            Id = "chars/goblin-boss",
            Name = "Goblin Boss",
            IsPc = true,
            CampaignName = "level-up-statblock",
            MaxHp = 21,
            CurrentHp = 21,
            SystemStats = new Dnd5eExtension
            {
                StatBlockHp = 21,
                HitDie = "d8",
                Level = 3,
            },
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new LevelUpChangeHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var summary = new List<string>();
        var ctx = CreateContext(session, "level-up-statblock", summary);
        ctx.RegisterNewCharacter(existing);

        var result = await handler.ApplyAsync(new LevelUpChange
        {
            CharacterId = "chars/goblin-boss",
            LevelsGained = 1,
        }, ctx);

        Assert.True(result.Success);
        Assert.Contains(summary, m => m.Contains("statBlockHp") && m.Contains("skipped formula HP gain"));
    }

    [Fact]
    public async Task SystemStatsPatch_AlwaysRunsNonHpBootstrap()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config("stats-patch"),
            ActiveSystem = RulesetSystem.Dnd5e,
        });

        var existing = new Character
        {
            Id = "chars/patched",
            Name = "Patched",
            MaxHp = 7,
            CurrentHp = 7,
            SystemStats = new Dnd5eExtension
            {
                StatBlockHp = 7,
                Dexterity = 10,
            },
        };
        await session.StoreAsync(existing);
        await session.SaveChangesAsync();

        var handler = new SystemStatsChangeHandler(keys, BootstrapTestHelper.CreateOrchestrator());
        var summary = new List<string>();
        var ctx = CreateContext(session, "stats-patch", summary);
        var result = await handler.ApplyAsync(new SystemStatsChange
        {
            CharacterId = "chars/patched",
            SystemStats = new Dnd5eExtension { Dexterity = 16 },
        }, ctx);

        Assert.True(result.Success);
        var stats = Assert.IsType<Dnd5eExtension>(existing.SystemStats);
        Assert.Equal(7, existing.MaxHp);
        Assert.Equal(13, stats.ArmorClass);
    }

    private static ChangeContext CreateContext(
        IAsyncDocumentSession session,
        string campaign,
        List<string> summary)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            session,
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            summary,
            dispatcher,
            null,
            campaign);
    }
}