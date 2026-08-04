using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class MultiCampaignIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public MultiCampaignIntegrationTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    private CampaignTools CreateTools() => TestCampaignToolsFactory.Create(_fixture);

    [Fact]
    public async Task CreateCampaign_Works_WithExplicitName()
    {
        var tools = CreateTools();
        var slug = "brand-new-world-" + Guid.NewGuid().ToString("N")[..8];

        var createResult = await tools.CreateCampaign(slug, RulesetSystem.Dnd5e);

        Assert.True(createResult.Success);
        Assert.NotNull(createResult.Data);
        Assert.Equal(slug, createResult.Data.Name);
    }

    [Fact]
    public async Task CreateCampaign_ThenGetCurrent_Works()
    {
        var tools = CreateTools();
        var slug = "brand-new-world-" + Guid.NewGuid().ToString("N")[..8];

        await tools.CreateCampaign(slug, RulesetSystem.Dnd5e);

        var currentResult = await tools.GetCurrentCampaign(slug);
        Assert.True(currentResult.Success);
        Assert.NotNull(currentResult.Data);
        Assert.Equal(slug, currentResult.Data.Campaign.Name);
    }

    [Fact]
    public async Task UpsertCharacter_BeforeCreateCampaign_PersistsDnd5eConfigAndWarnsInSummary()
    {
        var tools = CreateTools();
        var slug = "out-of-order-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        var upsertResult = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Early Bird" }, slug);

        Assert.True(upsertResult.Success);
        Assert.Contains("No campaign ruleset is configured yet", upsertResult.Summary);
        Assert.Contains("dnd5e", upsertResult.Summary);

        var configResult = await tools.GetConfig(slug);
        Assert.True(configResult.Success);
        Assert.Equal(RulesetSystem.Dnd5e, configResult.Data!.ActiveSystem);
    }

    [Fact]
    public async Task CreateCampaign_AfterEarlyUpsertCharacter_StillSucceedsAndCorrectsConfig()
    {
        var tools = CreateTools();
        var slug = "out-of-order-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        // Out-of-order: character created before the campaign is formally established.
        await tools.UpsertCharacter(new CharacterUpsertRequest { Id = charId, Name = "Early Bird" }, slug);

        var createResult = await tools.CreateCampaign(slug, RulesetSystem.Pathfinder2e);
        Assert.True(createResult.Success, $"create_campaign should still succeed: {createResult.Summary}");
        Assert.Equal(RulesetSystem.Pathfinder2e, createResult.Data!.System);

        // The config document, which is what character bootstrap actually reads, must now agree
        // with the locked Campaign.System rather than being stuck at the earlier implicit Dnd5e default.
        var configResult = await tools.GetConfig(slug);
        Assert.True(configResult.Success);
        Assert.Equal(RulesetSystem.Pathfinder2e, configResult.Data!.ActiveSystem);
    }

    [Fact]
    public async Task CreateCampaign_AfterEarlyGetWorldState_AdoptsPhantomMetaInsteadOfRefusing()
    {
        var repo = _fixture.CreateRepository();
        var exploration = TestCampaignToolsFactory.CreateTool<ExplorationTools>(_fixture, repo);
        var management = TestCampaignToolsFactory.CreateTool<CampaignManagementTools>(_fixture, repo);
        var slug = "phantom-world-" + Guid.NewGuid().ToString("N")[..8];

        // Out-of-order: a read tool (get_world_state, which underlies get_session_briefing) is
        // called against a slug that was never created via create_campaign. This auto-vivifies
        // bare Campaign/CampaignConfig/CampaignTime docs as a side effect of the read.
        var earlyRead = await exploration.GetWorldState(campaignName: slug);
        Assert.True(earlyRead.Success);

        // Regression: create_campaign used to reject this with "AlreadyExists" forever, because the
        // phantom Campaign meta doc already existed — permanently blocking real creation for the slug.
        var createResult = await management.CreateCampaign(slug, RulesetSystem.Pathfinder2e, loreYear: 900);
        Assert.True(createResult.Success, $"create_campaign should adopt the phantom instead of refusing: {createResult.Summary}");
        Assert.Equal(RulesetSystem.Pathfinder2e, createResult.Data!.System);
        Assert.True(createResult.Data.IsSystemLocked);
        Assert.Equal(900, createResult.Data.LoreSettings.Year);

        // The config doc (what bootstrap actually reads) must agree with the now-locked system.
        var configResult = await management.GetConfig(slug);
        Assert.True(configResult.Success);
        Assert.Equal(RulesetSystem.Pathfinder2e, configResult.Data!.ActiveSystem);

        // A second create_campaign call for the now-real, locked campaign must still be refused.
        var secondCreate = await management.CreateCampaign(slug, RulesetSystem.Dnd5e);
        Assert.False(secondCreate.Success);
        Assert.Equal("AlreadyExists", secondCreate.Error);
    }

    [Fact]
    public async Task UpsertCharacter_OnExistingId_WarnsInSummary_InsteadOfSilentOverwrite()
    {
        var tools = CreateTools();
        var slug = "collision-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        var firstResult = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Original", MaxHp = 20, CurrentHp = 20 }, slug);
        Assert.True(firstResult.Success);
        Assert.DoesNotContain("already existed", firstResult.Summary);

        var secondResult = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Sparse Re-Upsert" }, slug);

        Assert.True(secondResult.Success);
        Assert.Contains("already existed and was merged/overwritten", secondResult.Summary);
        Assert.Contains(charId, secondResult.Summary);
    }

    [Fact]
    public async Task UpsertCharacter_WithNonexistentCurrentLocationId_WarnsButSucceeds()
    {
        var tools = CreateTools();
        var slug = "dangling-ref-world-" + Guid.NewGuid().ToString("N")[..8];
        var charId = "chars/" + Guid.NewGuid().ToString("N")[..8];

        var result = await tools.UpsertCharacter(
            new CharacterUpsertRequest { Id = charId, Name = "Wanderer", CurrentLocationId = "locations/ghost-town" },
            slug);

        Assert.True(result.Success);
        Assert.Contains("currentLocationId 'locations/ghost-town' does not currently exist", result.Summary);
    }

    [Fact]
    public async Task UpsertQuest_WithNonexistentGiverAndRelatedIds_WarnsButSucceeds()
    {
        var repo = _fixture.CreateRepository();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "dangling-ref-world-" + Guid.NewGuid().ToString("N")[..8];
        var questId = "quests/" + Guid.NewGuid().ToString("N")[..8];

        var result = await worldBuilder.UpsertQuest(new QuestUpsertRequest
        {
            Id = questId,
            Title = "Find the Ghost Giver",
            GiverId = "chars/nonexistent-giver",
            RelatedFactionIds = ["factions/nonexistent-faction"]
        }, slug);

        Assert.True(result.Success);
        Assert.Contains("giverId='chars/nonexistent-giver'", result.Summary);
        Assert.Contains("relatedFactionIds[0]='factions/nonexistent-faction'", result.Summary);
    }

    [Fact]
    public async Task UpsertQuest_WithExistingReferences_NoWarning()
    {
        var repo = _fixture.CreateRepository();
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var slug = "dangling-ref-world-" + Guid.NewGuid().ToString("N")[..8];
        var giverId = "chars/" + Guid.NewGuid().ToString("N")[..8];
        var questId = "quests/" + Guid.NewGuid().ToString("N")[..8];

        await worldBuilder.UpsertCharacter(new CharacterUpsertRequest { Id = giverId, Name = "Real Giver" }, slug);
        var result = await worldBuilder.UpsertQuest(
            new QuestUpsertRequest { Id = questId, Title = "Real Quest", GiverId = giverId }, slug);

        Assert.True(result.Success);
        Assert.DoesNotContain("WARNING", result.Summary);
    }

    [Fact]
    public async Task LockIn_RejectionPath_PreventsRulesetChange()
    {
        var tools = CreateTools();

        await TestCampaignDefaults.EnsureExistsAsync(tools, "locked-world");

        // Setup initial campaign config with Dnd5e
        var configResult = await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, "locked-world");
        Assert.True(configResult.Success);

        // Try to change to Pathfinder2e, should fail due to lock
        var changeResult = await tools.SetActiveSystem(RulesetSystem.Pathfinder2e, null, "locked-world");
        Assert.False(changeResult.Success);
        Assert.Equal("SystemLocked", changeResult.Error);
    }

    [Fact]
    public async Task IndependentCampaigns_MaintainSeparateConfigsAndCombat()
    {
        var tools = CreateTools();
        var repo = _fixture.CreateRepository();

        // Upsert characters with explicit campaign for scoping (no BC for legacy needed)
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session,
                new CharacterUpsertRequest
                    { Id = "chars/char-1", Name = "Char 1", CurrentHp = 10, MaxHp = 10, KeepAlive = true },
                "campaign-a");
            await repo.UpsertCharacterAsync(session,
                new CharacterUpsertRequest
                    { Id = "chars/char-2", Name = "Char 2", CurrentHp = 10, MaxHp = 10, KeepAlive = true },
                "campaign-b");
            await session.SaveChangesAsync();
        }

        // Setup Campaign A (D&D 5e)
        await TestCampaignDefaults.EnsureExistsAsync(tools, "campaign-a");
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, "campaign-a");
        await tools.StartCombat("loc-1", ["chars/char-1"], "campaign-a");

        // Setup Campaign B (Pathfinder 2e)
        var createB = await tools.CreateCampaign("campaign-b", RulesetSystem.Pathfinder2e);
        Assert.True(createB.Success, createB.Summary);
        await tools.StartCombat("loc-2", ["chars/char-2"], "campaign-b");

        // Verify Campaign B
        var configB = await tools.GetConfig("campaign-b");
        Assert.NotNull(configB.Data);
        Assert.Equal(RulesetSystem.Pathfinder2e, configB.Data.ActiveSystem);
        var combatB = await tools.NextTurn(null, "campaign-b");
        Assert.NotNull(combatB.Data);
        Assert.Equal("chars/char-2", combatB.Data.Combatants[0].CharacterId);

        // Switch back to Campaign A (explicit campaignName — no session selection)
        var configA = await tools.GetConfig("campaign-a");
        Assert.NotNull(configA.Data);
        Assert.Equal(RulesetSystem.Dnd5e, configA.Data.ActiveSystem);
        var combatA = await tools.NextTurn(null, "campaign-a");
        Assert.NotNull(combatA.Data);
        Assert.Equal("chars/char-1", combatA.Data.Combatants[0].CharacterId);

        // === Scoping hardening verification (per plan + code_review.md) ===
        // Set high need on both for pressure test (loose filter for shareables, but here per-camp)
        using (var session = _store.OpenAsyncSession())
        {
            var cfgA = await session.LoadAsync<CampaignConfig>(new CampaignDocumentKeys().Config("campaign-a"));
            if (cfgA != null)
            {
                cfgA.MaxPressuresPerResponse = 50;
            }

            var cfgB = await session.LoadAsync<CampaignConfig>(new CampaignDocumentKeys().Config("campaign-b"));
            if (cfgB != null)
            {
                cfgB.MaxPressuresPerResponse = 50;
            }

            var c1 = await session.LoadAsync<Character>("chars/char-1");
            c1.Needs.ActiveNeeds["hunger"] = 95f; // triggers pressure for A
            var c2 = await session.LoadAsync<Character>("chars/char-2");
            c2.Needs.ActiveNeeds["hunger"] = 95f; // triggers for B
            await session.SaveChangesAsync();

            // Verify scoping set during upsert
            Assert.Equal("campaign-a", c1.CampaignName);
            Assert.Equal("campaign-b", c2.CampaignName);
        }

        // Pressures for camp A should include char-1, not char-2
        var wsA = await tools.GetWorldState("loc-1", "campaign-a");
        Assert.True(wsA.Success, $"GetWorldState failed: {wsA.Error} / {wsA.Summary}");
        var pressureTextA = string.Join(" | ", wsA.Data?.WorldPressure ?? []);
        Assert.Contains("Char 1", pressureTextA);
        Assert.DoesNotContain("Char 2", pressureTextA);

        // Direct pressure for B via contributor (to debug ws)
        using (var ps = _store.OpenAsyncSession())
        {
            var time = await repo.GetTimeAsync(ps, "campaign-b");
            var config = await repo.GetCampaignConfigAsync(ps, "campaign-b");
            var contributor = new CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor();
            var ctx = new CampaignVault.Data.Pressure.PressureContext("campaign-b", time, config, ps);
            var dps = await contributor.EvaluateAsync(ctx);
            var dpText = string.Join(" | ", dps.Select(p => p.Text));
            Assert.Contains("Char 2", dpText);
            Assert.DoesNotContain("Char 1", dpText);
        }

        // Pressures for camp B should include char-2 (if high), not char-1
        var wsB = await tools.GetWorldState("loc-2", "campaign-b");
        var pressureTextB = string.Join(" | ", wsB.Data?.WorldPressure ?? []);
        Assert.Contains("Char 2", pressureTextB);
        Assert.DoesNotContain("Char 1", pressureTextB);

        // Sim scoping: add schedules to both, set needs, advance only A, verify only A affected
        using (var session = _store.OpenAsyncSession())
        {
            var c1 = await session.LoadAsync<Character>("chars/char-1");
            c1.Schedule = new Schedule { DefaultLocationId = "loc-1", Routines = [] };
            c1.Needs.ActiveNeeds["tiredness"] = 50f;
            var c2 = await session.LoadAsync<Character>("chars/char-2");
            c2.Schedule = new Schedule { DefaultLocationId = "loc-2", Routines = [] };
            c2.Needs.ActiveNeeds["tiredness"] = 50f;
            await session.SaveChangesAsync();
        }

        await tools.AdvanceWorld(1, 9, "Advance A only", "campaign-a");

        using (var verify = _store.OpenAsyncSession())
        {
            var c1After = await verify.LoadAsync<Character>("chars/char-1");
            var c2After = await verify.LoadAsync<Character>("chars/char-2");
            // A should have changed (needs accumulation or schedule), B should not (or at least test no cross)
            // Since needs rule runs, tiredness may increase for scheduled; check A was processed
            // A may or may not have changed depending on rules/time (not strict for this test); main is B untouched by A advance
            // B should remain untouched by A's advance
            Assert.Equal(50f, c2After.Needs.ActiveNeeds["tiredness"]);
        }
    }

    [Fact]
    public async Task TwoMcpSessions_UseExplicitCampaignName_Independently()
    {
        var toolsA = TestCampaignToolsFactory.Create(_fixture);
        var toolsB = TestCampaignToolsFactory.Create(_fixture);
        var slugA = "session-a-" + Guid.NewGuid().ToString("N")[..8];
        var slugB = "session-b-" + Guid.NewGuid().ToString("N")[..8];

        await TestCampaignDefaults.EnsureExistsAsync(toolsA, slugA);
        await TestCampaignDefaults.EnsureExistsAsync(toolsB, slugB);

        var currentA = await toolsA.GetCurrentCampaign(slugA);
        var currentB = await toolsB.GetCurrentCampaign(slugB);

        Assert.True(currentA.Success);
        Assert.True(currentB.Success);
        Assert.Equal(slugA, currentA.Data!.Campaign.Name);
        Assert.Equal(slugB, currentB.Data!.Campaign.Name);
    }
}
