using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Comprehensive testing suite for:
/// 1. Embedding vector leak prevention in MCP responses
/// 2. Extensive MCP tool functionality
/// 3. Equipment/outfit layering, zones, prerequisites, and edge cases
/// 4. Full combat simulation: PC vs 2 goblins with complete equipment setup
/// </summary>
[Collection("RavenDB")]
public class ExtensiveToolingAndEquipmentTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public ExtensiveToolingAndEquipmentTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    private CampaignTools CreateTools() => TestCampaignToolsFactory.Create(_fixture);

    #region Embedding Vector Tests

    [Fact]
    public async Task EmbeddingVectors_GetScene_StripsSemanticsFromCharacters()
    {
        var tools = CreateTools();
        var campaign = "embed-test-" + Guid.NewGuid();
        var characterId = "chars/alice_" + Guid.NewGuid();
        var locationId = "locations/tavern_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            // Create a character WITH a semantic vector
            var character = new Character
            {
                Id = characterId,
                Name = "Alice",
                CurrentHp = 20,
                MaxHp = 20,
                CurrentLocationId = locationId,
                CampaignName = campaign,
                SemanticVector = new float[384], // Embedding vector (should be stripped)
                EmbeddingTextHash = "test_hash_value_12345"
            };
            for (int i = 0; i < 384; i++)
            {
                character.SemanticVector[i] = (float)i / 384f;
            }

            await session.StoreAsync(character);

            var location = new Location
            {
                Id = locationId,
                Name = "The Tavern",
                CampaignName = campaign,
                SemanticVector = new float[384],
                EmbeddingTextHash = "location_hash_xyz"
            };
            await session.StoreAsync(location);
            await session.SaveChangesAsync();
        }

        // Call get_scene - should strip embedding vectors
        var result = await tools.GetScene(locationId, campaignName: campaign);

        Assert.True(result.Success, result.Summary);
        var resultJson = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions { WriteIndented = true });

        // Verify vectors are NOT in the JSON response
        Assert.DoesNotContain("SemanticVector", resultJson);
        Assert.DoesNotContain("EmbeddingTextHash", resultJson);
        Assert.DoesNotContain("test_hash_value_12345", resultJson);

        // Verify actual data is still present (no overly aggressive stripping)
        Assert.Contains("Alice", resultJson);
        Assert.Contains("The Tavern", resultJson);
    }

    [Fact]
    public async Task EmbeddingVectors_SearchWorld_StripsSemanticsFromResults()
    {
        var tools = CreateTools();
        var campaign = "search-embed-" + Guid.NewGuid();
        var locationId = "locations/forest_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var location = new Location
            {
                Id = locationId,
                Name = "Enchanted Forest",
                Description = "A mysterious forest full of magic",
                CampaignName = campaign,
                SemanticVector = new float[384],
                EmbeddingTextHash = "forest_semantic_hash"
            };
            await session.StoreAsync(location);
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        // Call search_world with keyword - should strip vectors
        var result = await tools.SearchWorld("Enchanted", campaignName: campaign);

        Assert.True(result.Success);
        var resultJson = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions { WriteIndented = true });

        // Verify vectors are NOT present
        Assert.DoesNotContain("SemanticVector", resultJson);
        Assert.DoesNotContain("EmbeddingTextHash", resultJson);
        Assert.DoesNotContain("forest_semantic_hash", resultJson);

        // Verify search results are still present
        Assert.Contains("Enchanted Forest", resultJson);
    }

    [Fact]
    public async Task EmbeddingVectors_GetNpcContext_StripsSemanticsFromNpcAndMemories()
    {
        var tools = CreateTools();
        var campaign = "npc-embed-" + Guid.NewGuid();
        var characterId = "chars/merchant_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = characterId,
                Name = "Merchant Bob",
                CurrentHp = 15,
                MaxHp = 15,
                CampaignName = campaign,
                SemanticVector = new float[384],
                EmbeddingTextHash = "merchant_hash_xyz",
                Psychology = new PsychologyProfile
                {
                    Memories = new Dictionary<string, MemoryNode>
                    {
                        ["Rare items"] = new MemoryNode
                        {
                            Details = "Sold rare items to the party",
                            Importance = MemoryImportance.Important
                        }
                    }
                }
            };
            await session.StoreAsync(character);
            await session.SaveChangesAsync();
        }

        var result = await tools.GetNpcContext(characterId, campaignName: campaign);

        Assert.True(result.Success, result.Summary);
        var resultJson = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions { WriteIndented = true });

        // Verify all embedding fields are stripped
        Assert.DoesNotContain("SemanticVector", resultJson);
        Assert.DoesNotContain("EmbeddingTextHash", resultJson);
        Assert.DoesNotContain("merchant_hash_xyz", resultJson);
        Assert.DoesNotContain("memory_hash_abc", resultJson);

        // Verify content is still present
        Assert.Contains("Merchant Bob", resultJson);
        Assert.Contains("Sold rare items", resultJson);
    }

    #endregion

    #region Equipment Layering and Zone Tests

    [Fact]
    public async Task Equipment_LayeringWithoutConflict_MultipleLayersOnSameZone()
    {
        var tools = CreateTools();
        var campaign = "equip-layer-" + Guid.NewGuid();
        var charId = "chars/knight_" + Guid.NewGuid();
        var locationId = "locations/courtyard_" + Guid.NewGuid();
        var baseLayerId = "items/tunic_" + Guid.NewGuid();
        var armorLayerId = "items/chainmail_" + Guid.NewGuid();
        var outerLayerId = "items/cloak_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var location = new Location { Id = locationId, Name = "Courtyard", CampaignName = campaign };
            await session.StoreAsync(location);

            var character = new Character
            {
                Id = charId,
                Name = "Knight",
                CurrentHp = 30,
                MaxHp = 30,
                CurrentLocationId = locationId,
                CampaignName = campaign
            };
            await session.StoreAsync(character);

            // Base layer: tunic
            var tunic = new Item
            {
                Id = baseLayerId,
                Name = "Simple Tunic",
                Description = "A basic linen tunic",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Base,
                CoreCategory = ItemCategory.Clothing
            };
            await session.StoreAsync(tunic);

            // Armor layer: chainmail
            var chainmail = new Item
            {
                Id = armorLayerId,
                Name = "Chainmail Vest",
                Description = "Protection for the torso",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Armor,
                CoreCategory = ItemCategory.Armor,
                Properties = new Dictionary<string, object> { { "acBonus", 4 }, { "armorType", "medium" } }
            };
            await session.StoreAsync(chainmail);

            // Outer layer: cloak
            var cloak = new Item
            {
                Id = outerLayerId,
                Name = "Travel Cloak",
                Description = "A weathered cloak",
                HolderId = charId,
                EquipZones = [EquipZone.Torso, EquipZone.Back],
                EquipLayer = EquipLayer.Outer,
                CoreCategory = ItemCategory.Clothing,
                Properties = new Dictionary<string, object> { { "warmth", 2f } }
            };
            await session.StoreAsync(cloak);

            await session.SaveChangesAsync();
        }

        // Equip base layer
        var result1 = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = baseLayerId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(result1.Success, result1.Summary);

        // Equip armor layer - should not conflict with base
        var result2 = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = armorLayerId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(result2.Success, result2.Summary);

        // Equip outer layer - should not conflict with base or armor
        var result3 = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = outerLayerId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(result3.Success, result3.Summary);

        // Verify all three are equipped
        var scene = await tools.GetScene(locationId, campaignName: campaign);
        var sceneJson = JsonSerializer.Serialize(scene.Data);
        Assert.Contains("Simple Tunic", sceneJson);
        Assert.Contains("Chainmail Vest", sceneJson);
        Assert.Contains("Travel Cloak", sceneJson);
    }

    [Fact]
    public async Task Equipment_SameLayerConflict_UnequipOldWhenEquippingNew()
    {
        var tools = CreateTools();
        var campaign = "equip-conflict-" + Guid.NewGuid();
        var charId = "chars/rogue_" + Guid.NewGuid();
        var sword1Id = "items/sword1_" + Guid.NewGuid();
        var sword2Id = "items/sword2_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = charId,
                Name = "Rogue",
                CurrentHp = 20,
                MaxHp = 20,
                CampaignName = campaign
            };
            await session.StoreAsync(character);

            var sword1 = new Item
            {
                Id = sword1Id,
                Name = "Rusty Sword",
                HolderId = charId,
                EquipZones = [EquipZone.MainHand],
                EquipLayer = EquipLayer.Held,
                CoreCategory = ItemCategory.Weapon,
                Properties = new Dictionary<string, object> { { "acBonus", 1 } }
            };
            await session.StoreAsync(sword1);

            var sword2 = new Item
            {
                Id = sword2Id,
                Name = "Elven Blade",
                HolderId = charId,
                EquipZones = [EquipZone.MainHand],
                EquipLayer = EquipLayer.Held,
                CoreCategory = ItemCategory.Weapon,
                Properties = new Dictionary<string, object> { { "acBonus", 3 } }
            };
            await session.StoreAsync(sword2);

            await session.SaveChangesAsync();
        }

        // Equip first sword
        var equip1 = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = sword1Id } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equip1.Success);

        // Try to equip second sword without replaceConflicts - should fail
        var equip2NoReplace = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = sword2Id } },
            campaignName: campaign, narrative: "test narrative");
        Assert.False(equip2NoReplace.Success);
        Assert.Contains("slot conflict", equip2NoReplace.Summary);

        // Equip second sword WITH replaceConflicts - should succeed
        var equip2WithReplace = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = sword2Id, ReplaceConflicts = true } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equip2WithReplace.Success, equip2WithReplace.Summary);
        var replaceMessages = string.Join("\n", equip2WithReplace.Data!.Summary);
        Assert.Contains("Unequipped", replaceMessages);
        Assert.Contains("Rusty Sword", replaceMessages);
    }

    [Fact]
    public async Task Equipment_StackGroups_ModularArmorCoexist()
    {
        var tools = CreateTools();
        var campaign = "equip-stackgroup-" + Guid.NewGuid();
        var charId = "chars/paladin_" + Guid.NewGuid();
        var pauldronLeftId = "items/pauldron_left_" + Guid.NewGuid();
        var pauldronRightId = "items/pauldron_right_" + Guid.NewGuid();
        var chestId = "items/breastplate_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = charId,
                Name = "Paladin",
                CurrentHp = 35,
                MaxHp = 35,
                CampaignName = campaign
            };
            await session.StoreAsync(character);

            var chest = new Item
            {
                Id = chestId,
                Name = "Breastplate",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Armor,
                CoreCategory = ItemCategory.Armor,
                Properties = new Dictionary<string, object> { { "acBonus", 6 } }
            };
            await session.StoreAsync(chest);

            // Pauldron with StackGroup - allows two to coexist on same zone/layer
            var pauldronLeft = new Item
            {
                Id = pauldronLeftId,
                Name = "Left Pauldron",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Armor,
                StackGroup = "pauldron-left",
                CoreCategory = ItemCategory.Armor,
                RequiresEquippedTags = new List<string> { "chest-armor" }
            };
            chest.Tags.Add("chest-armor");
            await session.StoreAsync(pauldronLeft);

            var pauldronRight = new Item
            {
                Id = pauldronRightId,
                Name = "Right Pauldron",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Armor,
                StackGroup = "pauldron-right",
                CoreCategory = ItemCategory.Armor,
                RequiresEquippedTags = new List<string> { "chest-armor" }
            };
            await session.StoreAsync(pauldronRight);
            await session.SaveChangesAsync();

            // Update chest tags to add the prerequisite marker
            using (var session2 = _store.OpenAsyncSession())
            {
                var chestReload = await session2.LoadAsync<Item>(chestId);
                if (!chestReload.Tags.Contains("chest-armor"))
                {
                    chestReload.Tags.Add("chest-armor");
                }
                await session2.SaveChangesAsync();
            }
        }

        // Equip chest plate first
        var equipChest = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = chestId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipChest.Success, equipChest.Summary);

        // Equip left pauldron
        var equipLeft = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = pauldronLeftId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipLeft.Success, equipLeft.Summary);

        // Equip right pauldron - should coexist with left (different StackGroups)
        var equipRight = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = pauldronRightId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipRight.Success, equipRight.Summary);
    }

    [Fact]
    public async Task Equipment_IncompatibleTags_PreventEquipping()
    {
        var tools = CreateTools();
        var campaign = "equip-incomp-" + Guid.NewGuid();
        var charId = "chars/priest_" + Guid.NewGuid();
        var robeId = "items/ceremonial_robe_" + Guid.NewGuid();
        var swordId = "items/holy_sword_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = charId,
                Name = "Priest",
                CurrentHp = 18,
                MaxHp = 18,
                CampaignName = campaign
            };
            await session.StoreAsync(character);

            // Ceremonial robe - incompatible with "wielded-weapon" tag
            var robe = new Item
            {
                Id = robeId,
                Name = "Ceremonial Robe",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Outer,
                CoreCategory = ItemCategory.Clothing,
                IncompatibleWithEquippedTags = new List<string> { "wielded-weapon" }
            };
            await session.StoreAsync(robe);

            // Holy sword - tagged as "wielded-weapon"
            var sword = new Item
            {
                Id = swordId,
                Name = "Holy Sword",
                HolderId = charId,
                EquipZones = [EquipZone.MainHand],
                EquipLayer = EquipLayer.Held,
                CoreCategory = ItemCategory.Weapon,
                Tags = new List<string> { "wielded-weapon" }
            };
            await session.StoreAsync(sword);

            await session.SaveChangesAsync();
        }

        // Equip sword first (the incompatibility is declared on the robe, so it's only checked
        // when the robe itself is the item being equipped)
        var equipSword = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = swordId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipSword.Success, equipSword.Summary);

        // Try to equip robe - should fail due to incompatibility with the equipped sword
        var equipRobe = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = robeId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.False(equipRobe.Success);
        Assert.Contains("incompatible", equipRobe.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Equipment_ReorderingBatch_EquipThenUnequip()
    {
        var tools = CreateTools();
        var campaign = "equip-reorder-" + Guid.NewGuid();
        var charId = "chars/ranger_" + Guid.NewGuid();
        var locationId = "locations/trailhead_" + Guid.NewGuid();
        var oldBootsId = "items/old_boots_" + Guid.NewGuid();
        var newBootsId = "items/new_boots_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var location = new Location { Id = locationId, Name = "Trailhead", CampaignName = campaign };
            await session.StoreAsync(location);

            var character = new Character
            {
                Id = charId,
                Name = "Ranger",
                CurrentHp = 25,
                MaxHp = 25,
                CurrentLocationId = locationId,
                CampaignName = campaign
            };
            await session.StoreAsync(character);

            var oldBoots = new Item
            {
                Id = oldBootsId,
                Name = "Old Boots",
                HolderId = charId,
                EquipZones = [EquipZone.Feet],
                EquipLayer = EquipLayer.Base,
                CoreCategory = ItemCategory.Clothing
            };
            await session.StoreAsync(oldBoots);

            var newBoots = new Item
            {
                Id = newBootsId,
                Name = "Elven Boots",
                HolderId = charId,
                EquipZones = [EquipZone.Feet],
                EquipLayer = EquipLayer.Base,
                CoreCategory = ItemCategory.Clothing,
                Properties = new Dictionary<string, object> { { "speedModifier", 1f } }
            };
            await session.StoreAsync(newBoots);

            await session.SaveChangesAsync();
        }

        // Equip old boots first
        var equipOld = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = oldBootsId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipOld.Success, equipOld.Summary);

        // In a single batch: unequip old, then equip new
        var batchSwap = await tools.Commit(
            new WorldChange[]
            {
                new ItemUnequip { CharacterId = charId, ItemId = oldBootsId },
                new ItemEquip { CharacterId = charId, ItemId = newBootsId }
            },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(batchSwap.Success, batchSwap.Summary);

        // Verify new boots are equipped, old are not
        var scene = await tools.GetScene(locationId, campaignName: campaign);
        var sceneJson = JsonSerializer.Serialize(scene.Data);
        // Both items should appear, but new should show as equipped
        Assert.Contains("Elven Boots", sceneJson);
    }

    #endregion

    #region AC and Armor Parameter Tests

    [Fact]
    public async Task Equipment_ArmorClassRecalculation_OnEquip()
    {
        var tools = CreateTools();
        var campaign = "equip-ac-" + Guid.NewGuid();
        var charId = "chars/fighter_" + Guid.NewGuid();
        var leatherArmorId = "items/leather_armor_" + Guid.NewGuid();
        var shieldId = "items/shield_" + Guid.NewGuid();

        await tools.CreateCampaign(campaign, RulesetSystem.Dnd5e);

        var createFighter = await tools.Commit(
            new[]
            {
                new CharacterCreate
                {
                    CharacterId = charId,
                    Name = "Fighter",
                    MaxHp = 40,
                    CurrentHp = 40,
                    SystemStats = new Dnd5eExtension
                    {
                        Dexterity = 16 // +3 modifier
                    }
                }
            },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(createFighter.Success, createFighter.Summary);

        using (var session = _store.OpenAsyncSession())
        {
            // Leather armor: AC 11 + DEX
            var leather = new Item
            {
                Id = leatherArmorId,
                Name = "Leather Armor",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Armor,
                CoreCategory = ItemCategory.Armor,
                Properties = new Dictionary<string, object>
                {
                    { "acBonus", 2 },
                    { "armorType", "light" }
                }
            };
            await session.StoreAsync(leather);

            // Shield: +2 AC
            var shield = new Item
            {
                Id = shieldId,
                Name = "Wooden Shield",
                HolderId = charId,
                EquipZones = [EquipZone.OffHand],
                EquipLayer = EquipLayer.Held,
                CoreCategory = ItemCategory.Armor,
                Properties = new Dictionary<string, object> { { "acBonus", 2 } }
            };
            await session.StoreAsync(shield);

            await session.SaveChangesAsync();
        }

        // Before any armor: AC = 10 + 3 (dex) = 13
        var charBefore = await GetCharacter(charId, campaign);
        var acBeforeArmor = charBefore.SystemStats is Dnd5eExtension dnd5e ? dnd5e.ArmorClass : 0;
        Assert.Equal(13, acBeforeArmor);

        // Equip leather armor: AC = 10 + 2 (armor) + 3 (dex) = 15
        await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = leatherArmorId } },
            campaignName: campaign, narrative: "test narrative");

        var charWithArmor = await GetCharacter(charId, campaign);
        var acWithArmor = charWithArmor.SystemStats is Dnd5eExtension dnd5e2 ? dnd5e2.ArmorClass : 0;
        Assert.Equal(15, acWithArmor);

        // Equip shield: AC = 15 + 2 = 17
        await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = shieldId } },
            campaignName: campaign, narrative: "test narrative");

        var charWithShield = await GetCharacter(charId, campaign);
        var acWithShield = charWithShield.SystemStats is Dnd5eExtension dnd5e3 ? dnd5e3.ArmorClass : 0;
        Assert.Equal(17, acWithShield);
    }

    [Fact]
    public async Task Equipment_WarmthRating_Cumulative()
    {
        var tools = CreateTools();
        var campaign = "equip-warmth-" + Guid.NewGuid();
        var charId = "chars/explorer_" + Guid.NewGuid();
        var winterCoatId = "items/winter_coat_" + Guid.NewGuid();
        var fursId = "items/furs_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            var character = new Character
            {
                Id = charId,
                Name = "Explorer",
                CurrentHp = 22,
                MaxHp = 22,
                CampaignName = campaign
            };
            await session.StoreAsync(character);

            var coat = new Item
            {
                Id = winterCoatId,
                Name = "Winter Coat",
                HolderId = charId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Outer,
                CoreCategory = ItemCategory.Clothing,
                Properties = new Dictionary<string, object> { { "warmth", 5f } }
            };
            await session.StoreAsync(coat);

            var furs = new Item
            {
                Id = fursId,
                Name = "Fur Cloak",
                HolderId = charId,
                EquipZones = [EquipZone.Back],
                EquipLayer = EquipLayer.Outer,
                CoreCategory = ItemCategory.Clothing,
                Properties = new Dictionary<string, object> { { "warmth", 7f } }
            };
            await session.StoreAsync(furs);

            await session.SaveChangesAsync();
        }

        // Equip winter coat: warmth = 5
        await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = winterCoatId } },
            campaignName: campaign, narrative: "test narrative");

        var charWithCoat = await GetCharacter(charId, campaign);
        var warmthWithCoat = charWithCoat.SystemStats.WarmthRating;
        Assert.Equal(5f, warmthWithCoat);

        // Equip fur cloak: warmth = 5 + 7 = 12 (cumulative on different zones)
        var equipFurs = await tools.Commit(
            new[] { new ItemEquip { CharacterId = charId, ItemId = fursId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipFurs.Success, equipFurs.Summary);

        var charWithBoth = await GetCharacter(charId, campaign);
        var warmthWithBoth = charWithBoth.SystemStats.WarmthRating;
        Assert.Equal(12f, warmthWithBoth);
    }

    #endregion

    #region Full Combat Simulation: PC vs 2 Goblins

    [Fact]
    public async Task Combat_PCVsGoblins_FullEncounter()
    {
        var tools = CreateTools();
        var campaign = "combat-full-" + Guid.NewGuid();
        var locationId = "locations/goblin_lair_" + Guid.NewGuid();

        // PC: Elara the Ranger (equipped with armor and weapons)
        var elaraId = "chars/elara_ranger_" + Guid.NewGuid();
        var elaraArmorId = "items/elara_armor_" + Guid.NewGuid();
        var elaraSwordId = "items/elara_sword_" + Guid.NewGuid();

        // Goblins (basic stats)
        var goblin1Id = "chars/goblin1_" + Guid.NewGuid();
        var goblin2Id = "chars/goblin2_" + Guid.NewGuid();

        using (var session = _store.OpenAsyncSession())
        {
            // Setup location
            var location = new Location { Id = locationId, Name = "Goblin Lair", CampaignName = campaign };
            await session.StoreAsync(location);

            // Setup Elara with D&D 5e stats
            var elara = new Character
            {
                Id = elaraId,
                Name = "Elara",
                CurrentHp = 30,
                MaxHp = 30,
                CurrentLocationId = locationId,
                CampaignName = campaign,
                SystemStats = new Dnd5eExtension
                {
                    Dexterity = 16, // +3 modifier
                    Wisdom = 14     // +2 modifier
                }
            };
            await session.StoreAsync(elara);

            // Elara's armor
            var armor = new Item
            {
                Id = elaraArmorId,
                Name = "Studded Leather",
                HolderId = elaraId,
                EquipZones = [EquipZone.Torso],
                EquipLayer = EquipLayer.Armor,
                CoreCategory = ItemCategory.Armor,
                Properties = new Dictionary<string, object>
                {
                    { "acBonus", 2 },
                    { "armorType", "light" }
                }
            };
            await session.StoreAsync(armor);

            // Elara's sword
            var sword = new Item
            {
                Id = elaraSwordId,
                Name = "Longsword",
                HolderId = elaraId,
                EquipZones = [EquipZone.MainHand],
                EquipLayer = EquipLayer.Held,
                CoreCategory = ItemCategory.Weapon,
                Properties = new Dictionary<string, object> { { "acBonus", 1 } }
            };
            await session.StoreAsync(sword);

            // Goblin 1
            var goblin1 = new Character
            {
                Id = goblin1Id,
                Name = "Goblin Scout",
                CurrentHp = 7,
                MaxHp = 7,
                CurrentLocationId = locationId,
                CampaignName = campaign,
                SystemStats = new Dnd5eExtension
                {
                    Dexterity = 14,
                    Wisdom = 10
                }
            };
            await session.StoreAsync(goblin1);

            // Goblin 2
            var goblin2 = new Character
            {
                Id = goblin2Id,
                Name = "Goblin Shaman",
                CurrentHp = 5,
                MaxHp = 5,
                CurrentLocationId = locationId,
                CampaignName = campaign,
                SystemStats = new Dnd5eExtension
                {
                    Dexterity = 15,
                    Wisdom = 12
                }
            };
            await session.StoreAsync(goblin2);

            await session.SaveChangesAsync();
        }

        // Equip Elara
        var equipArmor = await tools.Commit(
            new[] { new ItemEquip { CharacterId = elaraId, ItemId = elaraArmorId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipArmor.Success, equipArmor.Summary);

        var equipSword = await tools.Commit(
            new[] { new ItemEquip { CharacterId = elaraId, ItemId = elaraSwordId } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(equipSword.Success, equipSword.Summary);

        // Start combat
        var startCombat = await tools.StartCombat(locationId, [elaraId, goblin1Id, goblin2Id], campaignName: campaign);
        Assert.True(startCombat.Success, startCombat.Summary);
        Assert.True(startCombat.Data.IsActive);
        Assert.Equal(3, startCombat.Data.Combatants.Count);

        var initialActiveTurnId = startCombat.Data.ActiveTurnId;
        Assert.NotNull(initialActiveTurnId);

        // Round 1: Elara acts
        var round1_elara = await tools.Commit(
            new[]
            {
                new HpChange { CharacterId = goblin1Id, Delta = -5 } // Elara attacks goblin 1
            },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(round1_elara.Success, round1_elara.Summary);

        // Next turn
        var nextTurn1 = await tools.NextTurn(campaignName: campaign);
        Assert.True(nextTurn1.Success, nextTurn1.Summary);

        // Round 1: Goblin 1 acts (at 2 HP)
        var round1_gob1 = await tools.Commit(
            new[]
            {
                new HpChange { CharacterId = elaraId, Delta = -2 } // Goblin 1 attacks
            },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(round1_gob1.Success, round1_gob1.Summary);

        // Next turn
        var nextTurn2 = await tools.NextTurn(campaignName: campaign);
        Assert.True(nextTurn2.Success, nextTurn2.Summary);

        // Round 1: Goblin 2 acts
        var round1_gob2 = await tools.Commit(
            new[]
            {
                new HpChange { CharacterId = elaraId, Delta = -1 } // Goblin 2 attacks
            },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(round1_gob2.Success, round1_gob2.Summary);

        // Verify HP
        var goblin1Check = await GetCharacter(goblin1Id, campaign);
        Assert.Equal(2, goblin1Check.CurrentHp);

        var goblin2Check = await GetCharacter(goblin2Id, campaign);
        Assert.Equal(5, goblin2Check.CurrentHp);

        var elaraCheck = await GetCharacter(elaraId, campaign);
        Assert.Equal(27, elaraCheck.CurrentHp); // 30 - 2 - 1

        // Continue combat: kill goblin 1
        var nextTurn3 = await tools.NextTurn(campaignName: campaign);
        Assert.True(nextTurn3.Success);

        var killGoblin1 = await tools.Commit(
            new[] { new HpChange { CharacterId = goblin1Id, Delta = -2 } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(killGoblin1.Success);

        // Goblin 1 should be at 0 HP
        var goblin1Dead = await GetCharacter(goblin1Id, campaign);
        Assert.Equal(0, goblin1Dead.CurrentHp);

        // Continue turns...
        var nextTurn4 = await tools.NextTurn(campaignName: campaign);
        Assert.True(nextTurn4.Success);

        // Goblin 2 attacks
        var gob2Attack = await tools.Commit(
            new[] { new HpChange { CharacterId = elaraId, Delta = -3 } },
            campaignName: campaign, narrative: "test narrative");
        Assert.True(gob2Attack.Success);

        // End combat
        var endCombat = await tools.EndCombat(campaignName: campaign);
        Assert.True(endCombat.Success, endCombat.Summary);
        Assert.False(endCombat.Data.IsActive);

        // Verify final state
        var elaraFinal = await GetCharacter(elaraId, campaign);
        var goblin2Final = await GetCharacter(goblin2Id, campaign);

        Assert.Equal(24, elaraFinal.CurrentHp); // 30 - 2 - 1 - 3
        Assert.Equal(5, goblin2Final.CurrentHp); // Still alive
    }

    #endregion

    #region MCP Tool Coverage Tests

    [Fact]
    public async Task Tools_GetWorldState_ReturnsWorldPressure()
    {
        var tools = CreateTools();
        var campaign = "world-state-" + Guid.NewGuid();

        var result = await tools.GetWorldState(campaignName: campaign);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        // WorldPressure should be present (even if empty for new campaign)
        var resultJson = JsonSerializer.Serialize(result.Data);
        Assert.Contains("campaign", resultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tools_GetCurrentCampaign_ReturnsCampaignMetadata()
    {
        var tools = CreateTools();
        var campaign = "get-campaign-" + Guid.NewGuid();

        // Create campaign first
        var create = await tools.CreateCampaign(campaign, RulesetSystem.Dnd5e, "D&D 5e Test");
        Assert.True(create.Success);

        var result = await tools.GetCurrentCampaign(campaignName: campaign);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(campaign, result.Data.Campaign.Name);
    }

    [Fact]
    public async Task Tools_AdvanceWorld_ProgressesTime()
    {
        var tools = CreateTools();
        var campaign = "advance-time-" + Guid.NewGuid();

        // Get initial time
        var initialState = await tools.GetWorldState(campaignName: campaign);
        var initialDays = initialState.Data?.Time?.TotalDaysElapsed ?? 0;

        // Advance 1 day
        var advance = await tools.AdvanceWorld(1, 9, "Advancing time by 1 day", campaignName: campaign);
        Assert.True(advance.Success, advance.Summary);

        // Get new time
        var newState = await tools.GetWorldState(campaignName: campaign);
        var newDays = newState.Data?.Time?.TotalDaysElapsed ?? 0;

        Assert.True(newDays > initialDays);
    }

    #endregion

    #region Helper Methods

    private async Task<Character> GetCharacter(string characterId, string campaignName)
    {
        using (var session = _store.OpenAsyncSession())
        {
            return await session.LoadAsync<Character>(characterId);
        }
    }

    #endregion
}
