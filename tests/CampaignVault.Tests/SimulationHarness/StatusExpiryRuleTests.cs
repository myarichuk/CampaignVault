using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests.SimulationHarness;

[Collection("RavenDB")]
public class StatusExpiryRuleTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store = fixture.Store;

    [Fact]
    public async Task ApplyAsync_ExpiresOnlyDayBasedStatuses()
    {
        var rule = new StatusExpiryRule(RulesetDataTestHelper.CreateConditionProvider());
        var charId = "char_" + Guid.NewGuid();

        var character = new Character
        {
            Id = charId,
            Name = "Bob",
            SystemStats = new Dnd5eExtension
            {
                StatusEffects =
                [
                    new StatusEffect { Name = "Poisoned", ExpiresAtDay = 5 }, // Should expire
                    new StatusEffect { Name = "Cursed", ExpiresAtDay = 20 }, // Should NOT expire yet
                    new StatusEffect { Name = "Stunned", ExpiresAtRound = 1 }
                ]
            }
        };

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(character);
            await session.SaveChangesAsync();
        }

        // Set simulation day to 10
        var simSession = _store.OpenAsyncSession();
        var simContext = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 10 },
            new List<Rumor>(),
            [character],
            simSession,
            1.0,
            "test_campaign"
        );

        var result = await rule.ApplyAsync(simContext, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Deltas); // Only "Poisoned" should be removed

        var removeDelta = result.Deltas.First() as StatusRemove;
        Assert.NotNull(removeDelta);
        Assert.Equal(charId, removeDelta.CharacterId);
        Assert.Equal("Poisoned", removeDelta.Status);
    }
}
