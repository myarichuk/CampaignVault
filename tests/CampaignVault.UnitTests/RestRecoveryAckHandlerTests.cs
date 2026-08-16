using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class RestRecoveryAckHandlerTests
{
    [Fact]
    public async Task ApplyAsync_SetsLastRestRecoveredDay()
    {
        var handler = new RestRecoveryAckHandler();
        var character = new Character { Id = "chars/wizard", LastRestedDay = 5 };

        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { ["chars/wizard"] = character },
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher([], new CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance));

        var result = await handler.ApplyAsync(
            new RestRecoveryAck { CharacterId = "chars/wizard", RestDay = 5, RestSequence = 3 },
            context,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, character.LastRestRecoveredDay);
        Assert.Equal(3, character.LastRecoveredRestSequence);
    }
}