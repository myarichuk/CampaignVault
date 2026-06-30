using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase6HandlersTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public Phase6HandlersTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LocationCreate_AutoLinksToParent_BothWays()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var parent = new Location { Id = "locations/parent", Name = "Parent", Exits = [] };
        await session.StoreAsync(parent);
        await session.SaveChangesAsync();

        var handler = new LocationCreateHandler();
        var change = new LocationCreate
        {
            LocationId = "locations/child",
            Name = "Child",
            ConnectedFromLocationId = "locations/parent",
            ConnectionDescription = "A sturdy oak door"
        };

        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = new ChangeContext(session, new Dictionary<string, Character>(), new Dictionary<string, Item>(),
            new Dictionary<string, Location> { { parent.Id, parent } }, new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(), NullLogger.Instance,
            [], dispatcher, null, "test-camp");

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        // Check if parent got the exit to the child (forward uses the connectionDescription)
        Assert.Single(parent.Exits);
        Assert.Equal("locations/child", parent.Exits[0].TargetLocationId);
        Assert.Equal("A sturdy oak door", parent.Exits[0].Description);

        // Save changes so we can load the child and verify
        await session.SaveChangesAsync();

        // Check if child got the reverse exit to the parent (derived)
        var child = await session.LoadAsync<Location>("locations/child");
        Assert.NotNull(child);
        Assert.Single(child.Exits);
        Assert.Equal("locations/parent", child.Exits[0].TargetLocationId);
        Assert.Equal("Leads back toward Parent (A sturdy oak door)", child.Exits[0].Description);
    }

    [Fact]
    public async Task CharacterCreate_InitializesHpAndSystemStats_BasedOnRuleset()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var keys = new CampaignDocumentKeys();
        var configId = keys.Config("test-camp-hp");
        var config = new CampaignConfig { Id = configId, ActiveSystem = RulesetSystem.Dnd5e };
        await session.StoreAsync(config);
        await session.SaveChangesAsync();

        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var change = new CharacterCreate
        {
            CharacterId = "characters/test-char-hp",
            Name = "Grog",
            MaxHp = 25
        };

        var dispatcher = new WorldChangeDispatcher([handler], new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var ctx = new ChangeContext(session, new Dictionary<string, Character>(), new Dictionary<string, Item>(),
            new Dictionary<string, Location>(), new Dictionary<string, Faction>(), new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [], dispatcher, null, "test-camp-hp");

        var result = await handler.ApplyAsync(change, ctx);
        Assert.True(result.Success);

        await session.SaveChangesAsync();

        var character = await session.LoadAsync<Character>("characters/test-char-hp");
        Assert.NotNull(character);
        Assert.Equal(25, character.MaxHp);
        Assert.Equal(25, character.CurrentHp);
        Assert.IsType<Dnd5eExtension>(character.SystemStats);
    }
}
