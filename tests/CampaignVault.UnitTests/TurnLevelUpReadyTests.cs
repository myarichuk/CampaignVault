using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 4 A1: the Unity client no longer re-reads start_session after every turn, so take_turn itself says
/// when a player character has earned a level (and only when XP moved or on a full turn).
/// </summary>
[Collection("RavenDB")]
public class TurnLevelUpReadyTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public TurnLevelUpReadyTests(RavenDBFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task XpGrant_SurfacesLevelUpReady_QuietDeltaTurnDoesNot()
    {
        var slug = "lvlready-" + Guid.NewGuid().ToString("N")[..8];
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var pcId = $"chars/{slug}-pc";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character
            {
                Id = pcId,
                Name = "PC",
                IsPc = true,
                CampaignName = slug,
                ClassLevel = "Fighter 1",
                MaxHp = 10,
                CurrentHp = 10
            });
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true,
                indexes: ["Character/Search"]);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Full turn with no XP movement: nothing earned yet, so nothing is listed.
        var first = await tools.TakeTurn(new TakeTurnRequest { Changes = [new EventOccurred { Category = EventCategory.Interaction, Summary = "A quiet word." }], Narrative = "A quiet word." }, slug);
        Assert.True(first.Success, first.Summary);
        Assert.Null(first.Data!.LevelUpReady);

        // 300 XP is 5e level 2: the turn that granted it says so.
        var granted = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new XpGrantChange { CharacterId = pcId, Amount = 300, Source = XpSource.Combat }],
            Narrative = "The PC earns the fight's experience."
        }, slug);
        Assert.True(granted.Success, granted.Summary);
        Assert.NotNull(granted.Data!.LevelUpReady);
        Assert.Contains(pcId, granted.Data.LevelUpReady!);

        // A quiet delta turn afterwards repeats nothing.
        var quiet = await tools.TakeTurn(new TakeTurnRequest { Changes = [new EventOccurred { Category = EventCategory.Interaction, Summary = "Another word." }], Narrative = "Another word." }, slug);
        Assert.True(quiet.Success, quiet.Summary);
        Assert.Equal(TurnMode.Delta, quiet.Data!.Mode);
        Assert.Null(quiet.Data.LevelUpReady);
    }
}
