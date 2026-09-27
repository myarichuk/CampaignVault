using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class SpellDefinitionTests
{
    private static readonly SpellDefinitionProvider Spells = new(
        Path.Combine(Path.GetTempPath(), "cv_spelldef_test_" + Guid.NewGuid()),
        typeof(SpellDefinitionProvider).Assembly);

    private static readonly ClassDefinitionProvider Classes = new(
        Path.Combine(Path.GetTempPath(), "cv_spelldef_cls_" + Guid.NewGuid()),
        typeof(ClassDefinitionProvider).Assembly);

    [Fact]
    public void Provider_LoadsDnd5eSpells_FromEmbeddedResources()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.Count >= 300, $"Expected full SRD spell corpus (~319), got {spells.Count}");
        Assert.True(spells.ContainsKey("fireball"));
        Assert.True(spells.ContainsKey("magic_missile"));
    }

    [Fact]
    public void Provider_LoadsPf2eSpells_FromEmbeddedResources()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(spells.Count >= 200, $"Expected expanded PF2e ORC corpus, got {spells.Count}");
        Assert.True(spells.ContainsKey("fireball"));
        Assert.True(spells.ContainsKey("detect_magic"));
    }

    [Fact]
    public void Provider_Fireball_HasCorrectMetadata()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("fireball", out var fireball));
        Assert.Equal(3, fireball.Level);
        Assert.False(fireball.Concentration ?? true);
        Assert.Contains("wizard", fireball.Classes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuerySpells_FiltersByClassAndLevel()
    {
        var level3Wizard = Spells.QuerySpells(RulesetSystem.Dnd5e, "Wizard", 3, Classes);

        Assert.Contains(level3Wizard, s => s.Name == "fireball");
        Assert.DoesNotContain(level3Wizard, s => s.Name == "magic_missile");
        Assert.All(level3Wizard, s => Assert.Equal(3, s.Level));
    }

    [Fact]
    public void Merge_ChildLevelWins_OverParent()
    {
        var parent = new SpellDefinition { Name = "base", Level = 1, Concentration = false };
        var child = new SpellDefinition { Name = "sub", Inherits = ["base"], Level = 3, Concentration = true };

        var merged = SpellDefinition.Merge(child, parent);

        Assert.Equal(3, merged.Level);
        Assert.True(merged.Concentration);
    }

    [Fact]
    public void SpellSlotValidator_RejectsOverLevelSpend()
    {
        var spell = new SpellDefinition { Name = "fireball", Level = 3 };
        var error = SpellSlotValidator.ValidateSpend(spell, slotLevel: 2);

        Assert.NotNull(error);
        Assert.Contains("fireball", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpellSlotValidator_ValidateSpend_SkipsCantrip()
    {
        // Cantrips are a soft warning at the handler level (SpellSlotValidator.CantripWarning),
        // not a hard failure from ValidateSpend, consistent with the other spell-validation paths.
        var spell = new SpellDefinition { Name = "fire_bolt", Level = 0 };

        Assert.True(SpellSlotValidator.IsCantrip(spell));
        Assert.Null(SpellSlotValidator.ValidateSpend(spell, slotLevel: 1));
    }

    [Fact]
    public async Task ResourceChangeHandler_ValidFireballSpend_Succeeds()
    {
        var handler = new ResourceChangeHandler(Spells);
        var character = MakeWizardWithSlots();

        var context = CreateContext(character);
        var change = new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = "spell_slots_3",
            Delta = -1,
            SpellName = "fireball",
            Reason = "Cast Fireball"
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.True(result.Success);
        Assert.Equal(3, character.SystemStats.ResourcePools["spell_slots_3"].Current);
    }

    [Fact]
    public void QueryPage_DefaultLimit_ReturnsFirstPageOnly()
    {
        var page = SpellQueryBuilder.QueryPage(
            Spells, RulesetSystem.Dnd5e, "Wizard", Classes, level: null, offset: 0, limit: null);

        Assert.True(page.TotalCount > SpellQueryBuilder.DefaultPageLimit);
        Assert.Equal(SpellQueryBuilder.DefaultPageLimit, page.Spells.Count);
        Assert.Equal(0, page.Offset);
        Assert.True(page.TotalCount > page.Spells.Count);
    }

    [Fact]
    public void QueryPage_WithOffset_ReturnsNextSlice()
    {
        var first = SpellQueryBuilder.QueryPage(
            Spells, RulesetSystem.Dnd5e, "Wizard", Classes, offset: 0, limit: 10);
        var second = SpellQueryBuilder.QueryPage(
            Spells, RulesetSystem.Dnd5e, "Wizard", Classes, offset: 10, limit: 10);

        Assert.Equal(10, first.Spells.Count);
        Assert.Equal(10, second.Spells.Count);
        Assert.NotEqual(first.Spells[0].Name, second.Spells[0].Name);
    }

    [Fact]
    public void GoldenSpells_Dnd5eFireball_AndPf2eDetectMagic_HaveExpectedMetadata()
    {
        var dnd = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);
        var pf2 = Spells.GetSpellsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(dnd.TryGetValue("fireball", out var fireball));
        Assert.Equal(3, fireball.Level);
        Assert.Contains("wizard", fireball.Classes, StringComparer.OrdinalIgnoreCase);

        Assert.True(dnd.TryGetValue("eldritch_blast", out var blast));
        Assert.Equal(0, blast.Level);
        Assert.Contains("warlock", blast.Classes, StringComparer.OrdinalIgnoreCase);

        Assert.True(pf2.TryGetValue("detect_magic", out var detect));
        Assert.Equal(0, detect.Level);
        Assert.Contains("wizard", detect.Classes, StringComparer.OrdinalIgnoreCase);

        Assert.True(pf2.TryGetValue("shield", out var shield));
        Assert.True(shield.Concentration);
    }

    [Fact]
    public async Task ResourceChangeHandler_SpellSpendWithoutSpellName_WarnsButSucceeds()
    {
        var handler = new ResourceChangeHandler(Spells);
        var character = MakeWizardWithSlots();
        var summary = new List<string>();
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [character.Id] = character },
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: summary,
            dispatcher: new WorldChangeDispatcher(
                [],
                new CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance));

        var result = await handler.ApplyAsync(new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = "spell_slots_3",
            Delta = -1,
            Reason = "Cast something"
        }, context);

        Assert.True(result.Success);
        Assert.Contains(summary, m => m.Contains("spellName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResourceChangeHandler_CantripSpend_WarnsButSucceeds()
    {
        var handler = new ResourceChangeHandler(Spells);
        var character = MakeWizardWithSlots();
        var summary = new List<string>();
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [character.Id] = character },
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: summary,
            dispatcher: new WorldChangeDispatcher(
                [],
                new CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance));

        var result = await handler.ApplyAsync(new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = "spell_slots_3",
            Delta = -1,
            SpellName = "fire_bolt",
            Reason = "Cast Fire Bolt by mistake"
        }, context);

        Assert.True(result.Success);
        Assert.Contains(summary, m => m.Contains("cantrip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResourceChangeHandler_WrongSlotLevel_Fails()
    {
        var handler = new ResourceChangeHandler(Spells);
        var character = MakeWizardWithSlots();

        var context = CreateContext(character);
        var change = new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = "spell_slots_2",
            Delta = -1,
            SpellName = "fireball"
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("fireball", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    private static ChangeContext CreateContext(params Character[] characters) =>
        new(
            sessionForTests: null,
            characters: characters.ToDictionary(c => c.Id),
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                [],
                new CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);

    [Fact]
    public async Task ResourceChangeHandler_SpendFromEmptyPool_ReturnsFailure()
    {
        var handler = new ResourceChangeHandler(Spells);
        var character = new Character
        {
            Id = "chars/test_character",
            Name = "Test Character",
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["test_pool"] = new() { Current = 0, Max = 5 }
                }
            }
        };

        var context = CreateContext(character);
        var change = new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = "test_pool",
            Delta = -3,
            Reason = "Spend from empty pool"
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("Insufficient", result.Message!);
        Assert.Equal(0, character.SystemStats.ResourcePools["test_pool"].Current);
    }

    [Fact]
    public async Task ResourceChangeHandler_SpendMoreThanAvailable_ReturnsFailure()
    {
        var handler = new ResourceChangeHandler(Spells);
        var character = new Character
        {
            Id = "chars/test_character",
            Name = "Test Character",
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["test_pool"] = new() { Current = 3, Max = 5 }
                }
            }
        };

        var context = CreateContext(character);
        var change = new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = "test_pool",
            Delta = -5,
            Reason = "Spend more than available"
        };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("Insufficient", result.Message!);
        Assert.Equal(3, character.SystemStats.ResourcePools["test_pool"].Current);
    }

    [Fact]
    public void Overlay_MagicMissile_HasInstanceData()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("magic_missile", out var missile));
        Assert.False(missile.RequiresAttackRoll ?? true);
        Assert.NotNull(missile.InstanceCountAtSlotLevel);
        Assert.Equal(3, missile.InstanceCountAtSlotLevel[1]);
        Assert.Equal(11, missile.InstanceCountAtSlotLevel[9]);
        Assert.Equal("1d4+1", missile.PerInstanceDamageAtSlotLevel![9]);
        // API's pre-summed total is kept as-is for reference.
        Assert.Equal("3d4 + 3", missile.DamageAtSlotLevel![1]);
    }

    [Fact]
    public void Overlay_ScorchingRay_HasPerRayData()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("scorching_ray", out var ray));
        Assert.Null(ray.RequiresAttackRoll);
        Assert.Equal(3, ray.InstanceCountAtSlotLevel![2]);
        Assert.Equal(10, ray.InstanceCountAtSlotLevel[9]);
        Assert.Equal("2d6", ray.PerInstanceDamageAtSlotLevel![2]);
    }

    [Fact]
    public void Overlay_EldritchBlast_HasCharacterLevelInstances()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("eldritch_blast", out var blast));
        Assert.Equal(1, blast.InstanceCountAtCharacterLevel![1]);
        Assert.Equal(2, blast.InstanceCountAtCharacterLevel[5]);
        Assert.Equal(3, blast.InstanceCountAtCharacterLevel[11]);
        Assert.Equal(4, blast.InstanceCountAtCharacterLevel[17]);
        Assert.Equal("1d10", blast.PerInstanceDamageAtCharacterLevel![17]);
    }

    [Fact]
    public void Overlay_AcidArrow_HasMissAndDelayedTick()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("acid_arrow", out var arrow));
        Assert.Equal(MissBehavior.Half, arrow.OnMiss);
        Assert.NotNull(arrow.DelayedTick);
        Assert.Equal(DelayedTickTrigger.EndOfTargetNextTurn, arrow.DelayedTick.TriggerAt);
        Assert.True(arrow.DelayedTick.RequiresInitialHit);
        Assert.Equal("acid", arrow.DelayedTick.DamageType);
        Assert.Equal("2d4", arrow.DelayedTick.DiceExpressionAtSlotLevel![2]);
        Assert.Equal("9d4", arrow.DelayedTick.DiceExpressionAtSlotLevel[9]);
    }

    [Fact]
    public void Overlay_Fireball_HasNoShapeData()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("fireball", out var fireball));
        Assert.Null(fireball.InstanceCountAtSlotLevel);
        Assert.Null(fireball.PerInstanceDamageAtSlotLevel);
        Assert.Null(fireball.RequiresAttackRoll);
        Assert.Null(fireball.OnMiss);
        Assert.Null(fireball.DelayedTick);
        Assert.Null(fireball.DamageIsPool);
        Assert.Null(fireball.DamagePools);
        Assert.Null(fireball.UpcastChoice);
    }

    [Fact]
    public void Overlay_Sleep_MarksPoolDamage()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("sleep", out var sleep));
        Assert.True(sleep.DamageIsPool);
        // The pool table itself is untouched API data.
        Assert.Equal("5d8", sleep.DamageAtSlotLevel![1]);
    }

    [Fact]
    public void Pools_IceStorm_HasApiDamagePools()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("ice_storm", out var storm));
        Assert.NotNull(storm.DamagePools);
        Assert.Equal(2, storm.DamagePools.Count);
        Assert.Equal("2d8", storm.DamagePools["bludgeoning"][4]);
        Assert.Equal("7d8", storm.DamagePools["bludgeoning"][9]);
        Assert.Equal("4d6", storm.DamagePools["cold"][4]);
        Assert.Equal("4d6", storm.DamagePools["cold"][9]);
        Assert.Null(storm.UpcastChoice);
        // Flat first-entry table is kept as-is for narrative/reference only.
        Assert.Equal("2d8", storm.DamageAtSlotLevel![4]);
    }

    [Fact]
    public void Pools_MeteorSwarm_HasApiDamagePools()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("meteor_swarm", out var swarm));
        Assert.NotNull(swarm.DamagePools);
        Assert.Equal(2, swarm.DamagePools.Count);
        Assert.Equal("20d6", swarm.DamagePools["fire"][9]);
        Assert.Equal("20d6", swarm.DamagePools["bludgeoning"][9]);
        Assert.Null(swarm.UpcastChoice);
    }

    [Fact]
    public void Overlay_FlameStrike_HasDamagePoolsAndUpcastChoice()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("flame_strike", out var strike));
        Assert.NotNull(strike.DamagePools);
        Assert.Equal(2, strike.DamagePools.Count);
        Assert.Equal("4d6", strike.DamagePools["fire"][5]);
        Assert.Equal("4d6", strike.DamagePools["fire"][9]);
        Assert.Equal("4d6", strike.DamagePools["radiant"][5]);
        Assert.NotNull(strike.UpcastChoice);
        Assert.Equal("1d6", strike.UpcastChoice.BonusDicePerSlot);
    }

    [Fact]
    public void Merge_ChildPoolsWin_OverParent()
    {
        var parent = new SpellDefinition { Name = "base" };
        var child = new SpellDefinition
        {
            Name = "sub",
            Inherits = ["base"],
            DamagePools = new Dictionary<string, Dictionary<int, string>>
            {
                ["fire"] = new() { [9] = "20d6" },
            },
            UpcastChoice = new UpcastPoolChoice { BonusDicePerSlot = "1d6" },
        };

        var merged = SpellDefinition.Merge(child, parent);

        Assert.NotNull(merged.DamagePools);
        Assert.Equal("20d6", merged.DamagePools["fire"][9]);
        Assert.Equal("1d6", merged.UpcastChoice!.BonusDicePerSlot);
    }

    private static Character MakeWizardWithSlots() =>
        new()
        {
            Id = "chars/test_wizard",
            Name = "Test Wizard",
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_2"] = new() { Current = 2, Max = 2 },
                    ["spell_slots_3"] = new() { Current = 4, Max = 4 },
                }
            }
        };
}