using Xunit;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using CampaignVault.Rulesets;
using Raven.Client.Documents;
using System.Threading.Tasks;
using System.Linq;
using System;

namespace CampaignVault.Tests.SimulationHarness;

[Collection("RavenDB")]
public class LazyLlmScenarios : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public LazyLlmScenarios(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        new Location_Search().Execute(_store);
        new Character_Search().Execute(_store);
    }

    [Fact]
    public async Task GetScene_EmptyFlavorVacuum_ProducesNarrativePrompt()
    {
        var repo = new CampaignRepository(_store);
        using (var session = _store.OpenAsyncSession())
        {
            var loc = new Location
            {
                Id = "locations/empty-room-" + Guid.NewGuid(),
                Name = "Empty Room",
                Description = "A completely bare room.",
                Type = LocationType.Room,
                CampaignName = "default"
            };
            await repo.UpsertLocationAsync(session, loc, "default");
            await session.SaveChangesAsync();

            var rollSvc = new CampaignVault.Data.DefaultRollService();
            var tools = new CampaignTools(repo, new DefaultBehaviorSynthesizer(), new RulesetResolverSelector(new IRulesetResolver[] { new Dnd5eRulesetResolver(rollSvc), new Pf2eRulesetResolver(rollSvc), new Fallout2d20RulesetResolver(rollSvc) }), new CampaignDocumentKeys(), new CurrentCampaignContext(), new PressureManager(new CampaignDocumentKeys()));

            var result = await tools.GetScene(loc.Id, partyPresent: true, campaignName: "default");
            
            Assert.True(result.Success);
            var pressures = result.WorldPressure;
            Assert.NotNull(pressures);
            Assert.Contains(pressures, p => p.Contains("lacks flavor"));
        }
    }

    [Fact]
    public async Task GetScene_MisspelledLocation_ProvidesSuggestions()
    {
        var repo = new CampaignRepository(_store);
        var rollSvc = new CampaignVault.Data.DefaultRollService();
        var tools = new CampaignTools(repo, new DefaultBehaviorSynthesizer(), new RulesetResolverSelector(new IRulesetResolver[] { new Dnd5eRulesetResolver(rollSvc), new Pf2eRulesetResolver(rollSvc), new Fallout2d20RulesetResolver(rollSvc) }), new CampaignDocumentKeys(), new CurrentCampaignContext(), new PressureManager(new CampaignDocumentKeys()));
        
        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = "cmp/1", Name = "LenientTest" });
            
            var loc = new Location
            {
                Id = "locations/pony-" + Guid.NewGuid(),
                Name = "The Prancing Pony",
                Description = "A well-known inn.",
                Type = LocationType.Room,
                CampaignName = "LenientTest"
            };
            await repo.UpsertLocationAsync(session, loc, "LenientTest");
            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene("Prancing Pony", false, "LenientTest");
        
        Assert.True(result.Success);
        var view = result.Data;
        Assert.False(view.IsLocationAnchored); // Hallucinated ID
        
        var pressures = result.WorldPressure;
        Assert.NotNull(pressures);
        Assert.Contains(pressures, p => p.Contains("Did you mean one of these:"));
        Assert.Contains(pressures, p => p.Contains("(The Prancing Pony)"));
    }
    [Fact]
    public async Task Commit_MisspelledCharacter_ProvidesSuggestions()
    {
        var repo = new CampaignRepository(_store);
        var rollSvc = new CampaignVault.Data.DefaultRollService();
        var tools = new CampaignTools(repo, new DefaultBehaviorSynthesizer(), new RulesetResolverSelector(new IRulesetResolver[] { new Dnd5eRulesetResolver(rollSvc), new Pf2eRulesetResolver(rollSvc), new Fallout2d20RulesetResolver(rollSvc) }), new CampaignDocumentKeys(), new CurrentCampaignContext(), new PressureManager(new CampaignDocumentKeys()));
        
        await tools.SelectCampaign("CharacterLenientTest");

        using (var session = _store.OpenAsyncSession())
        {
            var keys = new CampaignDocumentKeys();
            await session.StoreAsync(new Campaign { Id = keys.Meta("CharacterLenientTest"), Name = "CharacterLenientTest" });
            
            var character = new Character
            {
                Id = "chars/drizzzt",
                Name = "Drizzt Do'Urden",
                CampaignName = "CharacterLenientTest",
                CurrentHp = 10,
                MaxHp = 10
            };
            await session.StoreAsync(character);
            await session.SaveChangesAsync();
            
            // Wait for index
            await session.Query<Character, Character_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .ToListAsync();
        }

        var changes = new WorldChange[] 
        {
            new HpChange { CharacterId = "chars/drizz", Delta = -5 }
        };

        var result = await tools.Commit(changes, "Attack hits");
        
        Assert.False(result.Success);
        Assert.Contains("Did you mean: chars/drizzzt (Drizzt Do'Urden)?", result.Error);
    }
}
