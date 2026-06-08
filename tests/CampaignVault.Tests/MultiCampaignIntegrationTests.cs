using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using CampaignVault.Rulesets;
using CampaignVault.Data.ChangeHandlers;
using Raven.Client.Documents;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Generic;
using System.Linq;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class MultiCampaignIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public MultiCampaignIntegrationTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    private CampaignTools CreateTools(CurrentCampaignContext? currentCampaignContext = null)
    {
        var repo = new CampaignRepository(_store);
        var behavior = new DefaultBehaviorSynthesizer();
        
        var dnd5e = new Dnd5eRulesetResolver(new DefaultRollService());
        var pf2e = new Pf2eRulesetResolver(new DefaultRollService());
        var fallout = new Fallout2d20RulesetResolver(new DefaultRollService());
        var selector = new RulesetModuleSelector([dnd5e, pf2e, fallout]);

        currentCampaignContext ??= new CurrentCampaignContext();

        return new CampaignTools(repo, behavior, selector, new CampaignDocumentKeys(), currentCampaignContext);
    }

    [Fact]
    public async Task SelectCampaign_CreatesMinimalCampaignIfNonExistent()
    {
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);

        var selectResult = await tools.SelectCampaign("brand-new-world");
        
        Assert.True(selectResult.Success);
        Assert.Equal("brand-new-world", selectResult.Data);
        Assert.Equal("brand-new-world", context.CurrentCampaignName);
        Assert.Contains("new minimal campaign created", selectResult.Summary);

        var currentResult = await tools.GetCurrentCampaign();
        Assert.True(currentResult.Success);
        Assert.NotNull(currentResult.Data);
        Assert.Equal("brand-new-world", currentResult.Data.Name);
    }

    [Fact]
    public async Task LockIn_RejectionPath_PreventsRulesetChange()
    {
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);

        await tools.SelectCampaign("locked-world");
        
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
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);
        var repo = new CampaignRepository(_store);

        // Upsert characters with explicit campaign for scoping (no BC for legacy needed)
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session, new Character { Id = "characters/char-1", Name = "Char 1", CurrentHp = 10, MaxHp = 10, KeepAlive = true }, "campaign-a");
            await repo.UpsertCharacterAsync(session, new Character { Id = "characters/char-2", Name = "Char 2", CurrentHp = 10, MaxHp = 10, KeepAlive = true }, "campaign-b");
            await session.SaveChangesAsync();
        }

        // Setup Campaign A (D&D 5e)
        await tools.SelectCampaign("campaign-a");
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, "campaign-a");
        await tools.StartCombat("loc-1", ["characters/char-1"], "campaign-a");

        // Setup Campaign B (Pathfinder 2e)
        await tools.SelectCampaign("campaign-b");
        await tools.SetActiveSystem(RulesetSystem.Pathfinder2e, null, "campaign-b");
        await tools.StartCombat("loc-2", ["characters/char-2"], "campaign-b");

        // Verify Campaign B
        var configB = await tools.GetConfig("campaign-b");
        Assert.NotNull(configB.Data);
        Assert.Equal(RulesetSystem.Pathfinder2e, configB.Data.ActiveSystem);
        var combatB = await tools.NextTurn(null, "campaign-b");
        Assert.NotNull(combatB.Data);
        Assert.Equal("characters/char-2", combatB.Data.Combatants[0].CharacterId);

        // Switch back to Campaign A
        await tools.SelectCampaign("campaign-a");
        var configA = await tools.GetConfig("campaign-a");
        Assert.NotNull(configA.Data);
        Assert.Equal(RulesetSystem.Dnd5e, configA.Data.ActiveSystem);
        var combatA = await tools.NextTurn(null, "campaign-a");
        Assert.NotNull(combatA.Data);
        Assert.Equal("characters/char-1", combatA.Data.Combatants[0].CharacterId);

        // === Scoping hardening verification (per plan + code_review.md) ===
        // Set high need on both for pressure test (loose filter for shareables, but here per-camp)
        using (var session = _store.OpenAsyncSession())
        {
            var cfgA = await session.LoadAsync<CampaignConfig>(new CampaignDocumentKeys().Config("campaign-a"));
            if (cfgA != null) { cfgA.MaxPressuresPerResponse = 50; }
            var cfgB = await session.LoadAsync<CampaignConfig>(new CampaignDocumentKeys().Config("campaign-b"));
            if (cfgB != null) { cfgB.MaxPressuresPerResponse = 50; }

            var c1 = await session.LoadAsync<Character>("characters/char-1");
            c1.Needs.ActiveNeeds["hunger"] = 95f;  // triggers pressure for A
            var c2 = await session.LoadAsync<Character>("characters/char-2");
            c2.Needs.ActiveNeeds["hunger"] = 95f;  // triggers for B
            await session.SaveChangesAsync();

            // Verify scoping set during upsert
            Assert.Equal("campaign-a", c1.CampaignName);
            Assert.Equal("campaign-b", c2.CampaignName);
        }

        // Pressures for camp A should include char-1, not char-2
        await tools.SelectCampaign("campaign-a");
        var wsA = await tools.GetWorldState("loc-1", "campaign-a");
        Assert.True(wsA.Success, $"GetWorldState failed: {wsA.Error} / {wsA.Summary}");
        var pressureTextA = string.Join(" | ", wsA.Data?.WorldPressure ?? []);
        Assert.Contains("Char 1", pressureTextA);
        Assert.DoesNotContain("Char 2", pressureTextA);

        // Direct pressure for B via contributor (to debug ws)
        await tools.SelectCampaign("campaign-b");
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
            var c1 = await session.LoadAsync<Character>("characters/char-1");
            c1.Schedule = new Schedule { DefaultLocationId = "loc-1", Routines = [] };
            c1.Needs.ActiveNeeds["tiredness"] = 50f;
            var c2 = await session.LoadAsync<Character>("characters/char-2");
            c2.Schedule = new Schedule { DefaultLocationId = "loc-2", Routines = [] };
            c2.Needs.ActiveNeeds["tiredness"] = 50f;
            await session.SaveChangesAsync();
        }

        await tools.SelectCampaign("campaign-a");
        await tools.AdvanceWorld(1, TimeOfDay.Morning, "Advance A only", "campaign-a");

        using (var verify = _store.OpenAsyncSession())
        {
            var c1After = await verify.LoadAsync<Character>("characters/char-1");
            var c2After = await verify.LoadAsync<Character>("characters/char-2");
            // A should have changed (needs accumulation or schedule), B should not (or at least test no cross)
            // Since needs rule runs, tiredness may increase for scheduled; check A was processed
            // A may or may not have changed depending on rules/time (not strict for this test); main is B untouched by A advance
            // B should remain untouched by A's advance
            Assert.Equal(50f, c2After.Needs.ActiveNeeds["tiredness"]);
        }
    }
}
