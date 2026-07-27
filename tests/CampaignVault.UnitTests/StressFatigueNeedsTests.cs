using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class StressFatigueNeedsTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public StressFatigueNeedsTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private ChangeContext CreateContext(IAsyncDocumentSession session, Character character)
    {
        return new ChangeContext(
            session,
            new Dictionary<string, Character> { [character.Id] = character },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            () => Task.FromResult(new CampaignTime { TotalDaysElapsed = 10 }),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask,
            [],
            new WorldChangeDispatcher(new List<IWorldChangeHandler>(), new CampaignDocumentKeys()),
            null,
            "test-campaign"
        );
    }

    [Fact]
    public void NewCharacter_HasStressAndFatigueSeededInActiveNeeds()
    {
        var character = new Character { Id = "chars/npc", Name = "Npc" };

        Assert.True(character.Needs.ActiveNeeds.ContainsKey("stress"));
        Assert.True(character.Needs.ActiveNeeds.ContainsKey("fatigue"));
        Assert.Equal(0f, character.Needs.ActiveNeeds["stress"]);
        Assert.Equal(0f, character.Needs.ActiveNeeds["fatigue"]);
    }

    [Theory]
    [InlineData("stress")]
    [InlineData("fatigue")]
    public async Task NeedChangeHandler_AdjustsStressOrFatigue_LikeAnyOtherNeed(string need)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = new Character { Id = "chars/npc", Name = "Npc" };
        var ctx = CreateContext(session, character);
        var handler = new NeedChangeHandler();

        var result = await handler.ApplyAsync(
            new NeedChange { CharacterId = character.Id, Need = need, Delta = 35f }, ctx);

        Assert.True(result.Success);
        Assert.Equal(35f, character.Needs.ActiveNeeds[need]);
    }
}
