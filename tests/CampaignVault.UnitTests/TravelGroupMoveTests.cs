using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>F1/F2: same-origin, same-destination travelers in one commit move as a group.</summary>
public class TravelGroupMoveTests
{
    [Fact]
    public async Task TwoTravelers_ShareOneEncounterRoll()
    {
        var rolls = 0;
        var handler = new TravelChangeHandler(new EncounterResolver(() => { rolls++; return 1.0; })); // never interrupts
        var dispatcher = new WorldChangeDispatcher(
            [handler, new SpyHandler<EventOccurred>(), new SpyHandler<ActivityChange>(), new SpyHandler<NeedChange>()],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var gate = new Location
        {
            Id = "locations/gate",
            Name = "Gate",
            Exits = [new LocationExit("locations/street", "in", null, 1.0)]
        };
        var street = new Location { Id = "locations/street", Name = "Street" };
        var characters = new Dictionary<string, Character>
        {
            ["chars/a"] = new() { Id = "chars/a", Name = "A", CurrentLocationId = "locations/gate", IsPc = true },
            ["chars/b"] = new() { Id = "chars/b", Name = "B", CurrentLocationId = "locations/gate", IsPartyCompanion = true }
        };
        var context = ChangeContextTestHelper.Create(
            characters: characters,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location> { [gate.Id] = gate, [street.Id] = street },
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: dispatcher,
            activeCombat: null);

        foreach (var id in new[] { "chars/a", "chars/b" })
        {
            var result = await handler.ApplyAsync(
                new TravelChange { CharacterId = id, DestinationLocationId = street.Id }, context);
            Assert.True(result.Success);
        }

        // The helper hands out a fresh CampaignTime per call, so the once-per-group clock advance is
        // covered by the roll count: both come from the same non-follower branch.
        Assert.Equal(1, rolls);
    }
}
