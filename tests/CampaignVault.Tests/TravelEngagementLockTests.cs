using System.Collections.Generic;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class TravelEngagementLockTests
{
    [Fact]
    public async Task ApplyAsync_BlocksTravel_IfGrappled()
    {
        var character = new Character
        {
            Id = "char_1",
            Name = "Bram",
            SystemStats = new SystemExtension
            {
                EngagementRelations =
                [
                    new EngagementRelation
                    {
                        TargetId = "char_2",
                        Category = EngagementCategory.Physical,
                        Verb = "GrappledBy"
                    }
                ]
            }
        };
        var destination = new Location { Id = "loc_2", Name = "Forest" };

        var charDict = new Dictionary<string, Character> { { character.Id, character } };
        var locDict = new Dictionary<string, Location> { { destination.Id, destination } };

        var dispatcher = new WorldChangeDispatcher(
            new IWorldChangeHandler[] { new TravelChangeHandler(new EncounterResolver()) },
            new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance
        );

        var context = new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: locDict,
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            activeCombat: null,
            campaignName: null
        );

        var handler = new TravelChangeHandler(new EncounterResolver());
        var change = new TravelChange { CharacterId = "char_1", DestinationLocationId = "loc_2" };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("cannot travel because they are GrappledBy with character", result.Message);
    }
}
