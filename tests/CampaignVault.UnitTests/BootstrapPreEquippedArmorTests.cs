using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class BootstrapPreEquippedArmorTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public BootstrapPreEquippedArmorTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dnd5e_PreEquippedStartingArmor_DerivesRealAcImmediately()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var character = new Character
        {
            Id = "chars/bootstrap_armor_5e",
            Name = "Armored Recruit",
            SystemStats = new Dnd5eExtension { Dexterity = 14 }, // +2 mod
        };

        var armor = new Item
        {
            Id = "items/bootstrap_armor_5e_chainshirt",
            Name = "Chain Shirt",
            HolderId = character.Id,
            CoreCategory = ItemCategory.Armor,
            EquipZones = [EquipZone.Torso],
            EquipLayer = EquipLayer.Armor,
            IsEquipped = true,
            Properties = new Dictionary<string, object> { ["acBonus"] = "3", ["armorType"] = "medium" },
        };

        await session.StoreAsync(armor);
        await session.SaveChangesAsync();

        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var report = await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
            Session = session,
        });

        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
        // 10 + min(dex mod 2, medium cap 2) + acBonus 3 = 15
        Assert.Equal(15, stats.ArmorClass);
        Assert.Contains(report.Steps, s => s.StepName == "dnd5e.derive_defense");
        // No "worn armor not detected" hint should fire since armor was actually found.
        var defenseStep = report.Steps.Single(s => s.StepName == "dnd5e.derive_defense");
        Assert.DoesNotContain(defenseStep.LlmHints, h => h.Contains("not detected"));
    }

    [Fact]
    public async Task Pf2e_PreEquippedStartingArmor_DerivesRealAcImmediately()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var character = new Character
        {
            Id = "chars/bootstrap_armor_pf2e",
            Name = "Armored Recruit PF2e",
            SystemStats = new Pf2eExtension { DexterityMod = 2, Level = 1, AcProficiency = Pf2eProficiencyRank.Trained },
        };

        var armor = new Item
        {
            Id = "items/bootstrap_armor_pf2e_chainshirt",
            Name = "Chain Shirt",
            HolderId = character.Id,
            CoreCategory = ItemCategory.Armor,
            EquipZones = [EquipZone.Torso],
            EquipLayer = EquipLayer.Armor,
            IsEquipped = true,
            Properties = new Dictionary<string, object> { ["acBonus"] = "4" },
        };

        await session.StoreAsync(armor);
        await session.SaveChangesAsync();

        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Pathfinder2e,
            Session = session,
        });

        var stats = Assert.IsType<Pf2eExtension>(character.SystemStats);
        // 10 + dexMod(2) + (level 1 + trained 2) + acBonus 4 = 19
        Assert.Equal(19, stats.ArmorClass);
    }

    [Fact]
    public async Task Dnd5e_NoEquippedArmor_FallsBackToUnarmoredWithHint()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var character = new Character
        {
            Id = "chars/bootstrap_unarmored_5e",
            Name = "Unarmored Recruit",
            SystemStats = new Dnd5eExtension { Dexterity = 14 },
        };

        var orchestrator = BootstrapTestHelper.CreateOrchestrator();
        var report = await orchestrator.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = RulesetSystem.Dnd5e,
            Session = session,
        });

        var stats = Assert.IsType<Dnd5eExtension>(character.SystemStats);
        Assert.Equal(12, stats.ArmorClass);
        var defenseStep = report.Steps.Single(s => s.StepName == "dnd5e.derive_defense");
        Assert.Contains(defenseStep.LlmHints, h => h.Contains("not detected"));
    }
}
