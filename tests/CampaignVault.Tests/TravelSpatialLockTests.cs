using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class TravelSpatialLockTests
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
                SpatialRelations = new List<SpatialRelation> 
                { 
                    new() { TargetId = "char_2", RelationType = "GrappledBy" } 
                } 
            } 
        };
        var destination = new Location { Id = "loc_2", Name = "Forest" };

        var charDict = new Dictionary<string, Character> { { character.Id, character } };
        var locDict = new Dictionary<string, Location> { { destination.Id, destination } };

        var dispatcher = new WorldChangeDispatcher(
            new IWorldChangeHandler[] { new TravelChangeHandler() },
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

        var handler = new TravelChangeHandler();
        var change = new TravelChange { CharacterId = "char_1", DestinationLocationId = "loc_2" };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("cannot travel because they have a spatial relation 'GrappledBy'", result.Message);
    }
}
