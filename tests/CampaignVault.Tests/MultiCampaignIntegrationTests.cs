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
        var selector = new RulesetResolverSelector(new IRulesetResolver[] { dnd5e, pf2e, fallout });

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

        // Upsert characters
        using (var session = _store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(session, new Character { Id = "characters/char-1", Name = "Char 1", CurrentHp = 10, MaxHp = 10 });
            await repo.UpsertCharacterAsync(session, new Character { Id = "characters/char-2", Name = "Char 2", CurrentHp = 10, MaxHp = 10 });
            await session.SaveChangesAsync();
        }

        // Setup Campaign A (D&D 5e)
        await tools.SelectCampaign("campaign-a");
        await tools.SetActiveSystem(RulesetSystem.Dnd5e, null, "campaign-a");
        await tools.StartCombat("loc-1", new[] { "characters/char-1" }, "campaign-a");

        // Setup Campaign B (Pathfinder 2e)
        await tools.SelectCampaign("campaign-b");
        await tools.SetActiveSystem(RulesetSystem.Pathfinder2e, null, "campaign-b");
        await tools.StartCombat("loc-2", new[] { "characters/char-2" }, "campaign-b");

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
    }
}
