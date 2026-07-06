using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class EncounterResolverTests
{
    private ChangeContext CreateContext(Dictionary<string, string>? options = null)
    {
        options ??= new Dictionary<string, string>();
        
        var dispatcher = new WorldChangeDispatcher(
            [], 
            new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        var context = new ChangeContext(
            null!, // Session
            new Dictionary<string, Character>(),
            new Dictionary<string, Item>(), // Items
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher);
        
        context.GetSystemOptionsAsync = () => Task.FromResult(options);
        return context;
    }

    [Fact]
    public async Task EvaluateAsync_NoInterruption_WhenChanceIsZero()
    {
        // 0.99 > any clamped chance (max 0.90) -> will never interrupt
        var resolver = new EncounterResolver(() => 0.99);
        var ctx = CreateContext();
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Region };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Travel", "road");

        Assert.False(result.Interrupted);
        Assert.Equal(10, result.HoursPassed);
        Assert.Empty(result.Deltas);
        Assert.Empty(result.Narratives);
    }

    [Fact]
    public async Task EvaluateAsync_AlwaysInterrupts_WhenChanceIsHigh()
    {
        // 0.001 < any clamped chance (min 0.01) -> will always interrupt
        var resolver = new EncounterResolver(() => 0.001);
        var ctx = CreateContext();
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Wilderness };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Travel", "wilderness");

        Assert.True(result.Interrupted);
        Assert.Equal(4, result.HoursPassed); // Interrupted in the first 4-hour bucket
        Assert.NotEmpty(result.Deltas);
        Assert.Single(result.Narratives);
        Assert.Contains("interrupted after 4 hours", result.Narratives[0]);

        var eventOccurred = result.Deltas.OfType<EventOccurred>().FirstOrDefault();
        Assert.NotNull(eventOccurred);
        
        var charCreate = result.Deltas.OfType<CharacterCreate>().FirstOrDefault();
        Assert.NotNull(charCreate);
        Assert.Contains("Danger / Threat", charCreate.Notes); // 0.001 < 0.50 -> Danger/Threat
        
        var actChange = result.Deltas.OfType<ActivityChange>().FirstOrDefault();
        Assert.NotNull(actChange);
    }

    [Fact]
    public async Task EvaluateAsync_FactionBias_ReducesChance()
    {
        // Base chance for wilderness is 0.15. Faction bias reduces by 0.05 -> 0.10.
        // We roll 0.12, so no encounter. (If faction bias didn't apply, it would be an encounter).
        var resolver = new EncounterResolver(() => 0.12);
        var ctx = CreateContext();
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Wilderness, ControllingFactionId = "f1" };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Travel", "wilderness");

        Assert.False(result.Interrupted);
    }

    [Fact]
    public async Task EvaluateAsync_DangerModifier_IncreasesChance()
    {
        // Base chance for road is 0.05. We roll 0.08. Normal = no encounter.
        // Danger modifier = 20 -> +0.10. New chance = 0.15. Encounter happens!
        var resolver = new EncounterResolver(() => 0.08);
        var ctx = CreateContext();
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Region, DangerModifier = 20 };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Travel", "road");

        Assert.True(result.Interrupted);
    }

    [Fact]
    public async Task EvaluateAsync_UserModifier_IncreasesChance()
    {
        // Base chance for road is 0.05. We roll 0.08. Normal = no encounter.
        // User modifier = 20 -> +0.10. New chance = 0.15. Encounter happens!
        var resolver = new EncounterResolver(() => 0.08);
        var ctx = CreateContext();
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Region };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 20, "Travel", "road");

        Assert.True(result.Interrupted);
    }

    [Fact]
    public async Task EvaluateAsync_SystemOptionsOverride_UsedInsteadOfDefaults()
    {
        // Override Base chance for road to 0.50. We roll 0.40. Normal = 0.05 (no encounter). Override = encounter!
        var resolver = new EncounterResolver(() => 0.40);
        var options = new Dictionary<string, string> { { "TravelEncounter_Region", "0.50" } };
        var ctx = CreateContext(options);
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Region };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Travel", "road");

        Assert.True(result.Interrupted);
    }

    [Fact]
    public async Task EvaluateAsync_ContextTypeRest_UsesRestOptions()
    {
        // Base chance for room is 0.02. Roll 0.10 = no encounter.
        // Override RestEncounter_Room = 0.20. Roll 0.10 = encounter!
        var resolver = new EncounterResolver(() => 0.10);
        var options = new Dictionary<string, string> { { "RestEncounter_Room", "0.20" } };
        var ctx = CreateContext(options);
        var character = new Character { Id = "c1", Name = "Test" };
        var location = new Location { Id = "l1", Type = LocationType.Room };

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Rest");

        Assert.True(result.Interrupted);
    }

    [Fact]
    public async Task EvaluateSceneInterruptAsync_TriggersOnLowRoll()
    {
        var resolver = new EncounterResolver(() => 0.0);
        var ctx = CreateContext();
        var character = new Character
        {
            Id = "chars/valen",
            Name = "Valen",
            VisualTags = ["bloody", "wanted"],
            CurrentAppearance = "Covered in blood"
        };
        var location = new Location
        {
            Id = "locations/hall",
            Name = "Training Hall",
            Type = LocationType.Building,
            AmbientCrowd = "25 mercenaries"
        };

        var result = await resolver.EvaluateSceneInterruptAsync(ctx, character, location, 25, 10, "Crowd tense");

        Assert.True(result.Interrupted);
        var ev = result.Deltas.OfType<EventOccurred>().FirstOrDefault();
        Assert.NotNull(ev);
        Assert.Equal(EventCategory.SceneInterrupt, ev!.Category);
        var created = result.Deltas.OfType<CharacterCreate>().FirstOrDefault();
        Assert.NotNull(created);
        Assert.Contains("ambient crowd", created!.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Crowd:", created.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvaluateSceneInterruptAsync_NoInterruptOnHighRoll()
    {
        var resolver = new EncounterResolver(() => 0.99);
        var ctx = CreateContext();
        var character = new Character { Id = "chars/valen" };
        var location = new Location { Id = "locations/hall", Type = LocationType.Room };

        var result = await resolver.EvaluateSceneInterruptAsync(ctx, character, location, 0);

        Assert.False(result.Interrupted);
        Assert.Empty(result.Deltas);
    }

    [Theory]
    [InlineData(0.25, "Danger / Threat")]
    [InlineData(0.60, "Social / Neutral")]
    [InlineData(0.80, "Opportunity / Boon")]
    [InlineData(0.95, "Consequence / Reputation")]
    public async Task RollEncounterCategory_GeneratesCorrectCategory(double categoryRoll, string expectedCategory)
    {
        // First roll (chance) is 0.0 to guarantee an encounter.
        // Second roll (category) is the parameter.
        var rolls = new Queue<double>([0.0, categoryRoll]);
        var resolver = new EncounterResolver(() => rolls.Dequeue());

        var ctx = CreateContext();
        var character = new Character { Id = "c1" };
        var location = new Location { Id = "l1" }; 

        var result = await resolver.EvaluateAsync(ctx, character, location, 10, 4, 0, "Rest");
        
        var charCreate = result.Deltas.OfType<CharacterCreate>().FirstOrDefault();
        Assert.NotNull(charCreate);
        Assert.Contains(expectedCategory, charCreate.Notes);
    }
}
