using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Migrations;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>The exact JSON a model sends must be able to record a fighting style, and the data fixer must restore missing class pools.</summary>
[Collection("RavenDB")]
public class RecordedChoicesWiringTests(RavenDbTestEnvironment environment)
{
    private readonly CampaignDocumentKeys _keys = new();

    [Fact]
    public void CharacterUpdateJson_WithLevelUpChoices_MergesIntoTheSheet_AndArchersFeelIt()
    {
        const string json = """
        { "$type": "character_update", "characterId": "hank",
          "systemStats": { "$system": "dnd5e",
            "levelUpChoices": [ { "level": 1, "key": "fightingStyle", "value": "archery" } ],
            "feats": [ "grappler" ] } }
        """;
        var update = Assert.IsType<CharacterUpdate>(JsonSerializer.Deserialize<WorldChange>(json));
        var sheet = new Dnd5eExtension { Dexterity = 18, Level = 4 };

        var merged = Assert.IsType<Dnd5eExtension>(SystemStatsMerger.Merge(
            sheet, SystemStatsMerger.CoerceToRuleset(update.SystemStats!, RulesetSystem.Dnd5e), RulesetSystem.Dnd5e));

        var choice = Assert.Single(merged.LevelUpChoices);
        Assert.Equal("fightingStyle", choice.Key);
        Assert.True(CombatFeatureRules.HasFightingStyle(merged, "archery"));
        Assert.True(CombatFeatureRules.HasFeat(merged, "grappler"));
    }

    [Fact]
    public void CharacterUpdateJson_ChoicesAreAppendedNotReplaced_OnALaterPatch()
    {
        var sheet = new Dnd5eExtension();
        sheet.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = 1, Key = "fightingStyle", Value = "archery" });
        const string json = """
        { "$type": "character_update", "characterId": "hank",
          "systemStats": { "$system": "dnd5e", "levelUpChoices": [ { "level": 3, "key": "subclass", "value": "champion" } ] } }
        """;
        var update = Assert.IsType<CharacterUpdate>(JsonSerializer.Deserialize<WorldChange>(json));

        var merged = Assert.IsType<Dnd5eExtension>(SystemStatsMerger.Merge(
            sheet, SystemStatsMerger.CoerceToRuleset(update.SystemStats!, RulesetSystem.Dnd5e), RulesetSystem.Dnd5e));

        Assert.Contains(merged.LevelUpChoices, c => c.Key == "fightingStyle");
        Assert.Contains(merged.LevelUpChoices, c => c.Key == "subclass");
    }

    [Fact]
    public async Task PoolRepair_AddsMissingClassPools_KeepsExistingOnes_AndIsIdempotent()
    {
        var (store, _) = environment.CreateStoreForClass($"PoolRepair_{Guid.NewGuid():N}");
        var campaign = "pool-repair-" + Guid.NewGuid().ToString("N")[..6];
        var hankId = "chars/pool-repair-hank";
        var goblinId = "chars/pool-repair-goblin";

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignConfig { Id = _keys.Config(campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
            var hank = new Character
            {
                Id = hankId, Name = "Hank", CampaignName = campaign, KeepAlive = true, ClassLevel = "Fighter 4 / Scout Rogue 3",
                SystemStats = new Dnd5eExtension { Level = 7 },
            };
            hank.SystemStats.ResourcePools["gold"] = new ResourcePool { Current = 42, Max = 1000000 };
            await session.StoreAsync(hank, hankId, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character
            {
                Id = goblinId, Name = "Goblin", CampaignName = campaign, MaxHp = 7,
                SystemStats = new Dnd5eExtension { StatBlockHp = 7 },
            }, goblinId, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var initializer = RulesetDataTestHelper.CreateServices().Initializer;
        var repair = new RepairMissingResourcePools(store, initializer);

        var (_, details) = await repair.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.Contains(details, d => d.Contains(hankId) && d.Contains("+action_surge") && d.Contains("+second_wind"));
        Assert.DoesNotContain(details, d => d.Contains(goblinId));

        using (var read = store.OpenAsyncSession())
        {
            var hank = await read.LoadAsync<Character>(hankId, TestContext.Current.CancellationToken);
            var pools = hank!.SystemStats!.ResourcePools;
            Assert.Equal(1, pools["action_surge"].Max);
            Assert.Equal(42, pools["gold"].Current);   // existing pool untouched
            var goblin = await read.LoadAsync<Character>(goblinId, TestContext.Current.CancellationToken);
            Assert.Empty(goblin!.SystemStats!.ResourcePools);
        }

        var (_, second) = await repair.ExecuteAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(second, d => d.Contains(hankId));
    }
}
