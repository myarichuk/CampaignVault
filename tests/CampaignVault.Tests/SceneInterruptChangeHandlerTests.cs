using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class SceneInterruptChangeHandlerTests
{
    private static ChangeContext CreateContext(
        Character character,
        Location location,
        IEnumerable<Character>? otherNpcs = null,
        CombatEncounter? activeCombat = null,
        WorldChangeDispatcher? dispatcher = null)
    {
        var characters = new Dictionary<string, Character> { [character.Id] = character };
        foreach (var npc in otherNpcs ?? [])
        {
            characters[npc.Id] = npc;
        }

        dispatcher ??= new WorldChangeDispatcher(
            [],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        return new ChangeContext(
            null!,
            characters,
            new Dictionary<string, Item>(),
            new Dictionary<string, Location> { [location.Id] = location },
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            activeCombat);
    }

    [Fact]
    public async Task ApplyAsync_WhenRollSucceeds_SpawnsCrowdFigure()
    {
        var resolver = new EncounterResolver(() => 0.0);
        var handler = new SceneInterruptChangeHandler(resolver);
        var dispatched = new List<WorldChange>();
        var dispatcher = new WorldChangeDispatcher(
            [new CapturingHandler(dispatched)],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);

        var character = new Character
        {
            Id = "chars/valen",
            Name = "Valen",
            CurrentLocationId = "locations/hall",
            VisualTags = ["bloody", "wanted"]
        };
        var location = new Location
        {
            Id = "locations/hall",
            Name = "Training Hall",
            Type = LocationType.Building,
            AmbientCrowd = "25 warriors and mercenaries"
        };

        var result = await handler.ApplyAsync(new SceneInterruptCheck
        {
            CharacterId = "chars/valen",
            LocationId = "locations/hall",
            RiskModifier = 30,
            Notes = "Famous wanted face"
        }, CreateContext(character, location, dispatcher: dispatcher), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("INTERRUPT", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(dispatched, d => d is EventOccurred e && e.Category == EventCategory.SceneInterrupt);
        Assert.Contains(dispatched, d => d is CharacterCreate);
    }

    [Fact]
    public async Task ApplyAsync_WhenRollFails_ReturnsNoReaction()
    {
        var resolver = new EncounterResolver(() => 0.99);
        var handler = new SceneInterruptChangeHandler(resolver);
        var character = new Character
        {
            Id = "chars/valen",
            Name = "Valen",
            CurrentLocationId = "locations/hall"
        };
        var location = new Location
        {
            Id = "locations/hall",
            Name = "Hall",
            AmbientCrowd = "A crowd"
        };

        var result = await handler.ApplyAsync(new SceneInterruptCheck
        {
            CharacterId = "chars/valen",
            LocationId = "locations/hall"
        }, CreateContext(character, location), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("no reaction", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_FailsWithoutCrowdContext()
    {
        var handler = new SceneInterruptChangeHandler(new EncounterResolver(() => 0.0));
        var character = new Character
        {
            Id = "chars/valen",
            Name = "Valen",
            CurrentLocationId = "locations/clearing"
        };
        var location = new Location { Id = "locations/clearing", Name = "Clearing" };

        var result = await handler.ApplyAsync(new SceneInterruptCheck
        {
            CharacterId = "chars/valen",
            LocationId = "locations/clearing"
        }, CreateContext(character, location), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ambientCrowd", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_FailsDuringActiveCombat()
    {
        var handler = new SceneInterruptChangeHandler(new EncounterResolver(() => 0.0));
        var character = new Character
        {
            Id = "chars/valen",
            Name = "Valen",
            CurrentLocationId = "locations/hall"
        };
        var location = new Location
        {
            Id = "locations/hall",
            Name = "Hall",
            AmbientCrowd = "Crowd"
        };

        var result = await handler.ApplyAsync(
            new SceneInterruptCheck { CharacterId = "chars/valen", LocationId = "locations/hall" },
            CreateContext(character, location, activeCombat: new CombatEncounter { Id = "combat/1", LocationId = "locations/hall" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("combat", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyAsync_AllowsThreePresentNpcsWithoutAmbientCrowd()
    {
        var resolver = new EncounterResolver(() => 0.0);
        var handler = new SceneInterruptChangeHandler(resolver);
        var character = new Character
        {
            Id = "chars/valen",
            Name = "Valen",
            CurrentLocationId = "locations/plaza"
        };
        var location = new Location { Id = "locations/plaza", Name = "Plaza" };
        var others = new[]
        {
            new Character { Id = "chars/a", CurrentLocationId = "locations/plaza" },
            new Character { Id = "chars/b", CurrentLocationId = "locations/plaza" },
            new Character { Id = "chars/c", CurrentLocationId = "locations/plaza" }
        };

        var result = await handler.ApplyAsync(new SceneInterruptCheck
        {
            CharacterId = "chars/valen",
            LocationId = "locations/plaza",
            RiskModifier = 40
        }, CreateContext(character, location, others), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("INTERRUPT", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingHandler : IWorldChangeHandler
    {
        private readonly List<WorldChange> _captured;

        public CapturingHandler(List<WorldChange> captured) => _captured = captured;

        public bool ShouldHandle(WorldChange change) => true;

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
        {
            _captured.Add(change);
            return Task.FromResult(ChangeHandlerResult.Ok);
        }
    }
}