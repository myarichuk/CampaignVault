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
            [new TravelChangeHandler(new EncounterResolver())],
            new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance
        );

        var context = ChangeContextTestHelper.Create(
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: locDict,
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            activeCombat: null
        );

        var handler = new TravelChangeHandler(new EncounterResolver());
        var change = new TravelChange { CharacterId = "char_1", DestinationLocationId = "loc_2" };

        var result = await handler.ApplyAsync(change, context);

        Assert.False(result.Success);
        Assert.Contains("cannot travel because they are GrappledBy with character", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_HardEngagement_AllowsTravel_WhenTheTargetTravelsAlongInTheSameBatch()
    {
        var captive = new Character
        {
            Id = "chars/captive",
            Name = "Captive",
            SystemStats = new SystemExtension
            {
                EngagementRelations =
                [
                    new EngagementRelation { TargetId = "chars/guard", Category = EngagementCategory.Physical, Verb = "chained to" }
                ]
            }
        };
        var destination = new Location { Id = "loc_2", Name = "Road" };
        var dispatcher = new WorldChangeDispatcher(
            [new TravelChangeHandler(new EncounterResolver())],
            new CampaignVault.Data.CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var context = ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [captive.Id] = captive },
            locations: new Dictionary<string, Location> { [destination.Id] = destination },
            dispatcher: dispatcher);
        var alone = new TravelChange { CharacterId = "chars/captive", DestinationLocationId = "loc_2" };
        var handler = new TravelChangeHandler(new EncounterResolver());

        context.Batch = [alone];
        Assert.False((await handler.ApplyAsync(alone, context)).Success);

        context.Batch = [new TravelChange { CharacterId = "chars/guard", DestinationLocationId = "loc_2" }, alone];
        var together = await handler.ApplyAsync(alone, context);
        Assert.DoesNotContain("cannot travel because", together.Message ?? "");
    }
}
