using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Slowness that is temporary (a hobble, a chain, a poison) stretches the trip; gear and haste do not shorten it.</summary>
public class TravelSpeedTests
{
    private static Character Traveler(string id, float? movement, params (string Name, float Speed)[] effects)
    {
        var c = new Character
        {
            Id = id, Name = id, CurrentLocationId = "locations/gate", IsPc = true,
            SystemStats = new Dnd5eExtension { Movement = movement },
        };
        foreach (var (name, speed) in effects)
            c.SystemStats.StatusEffects.Add(new StatusEffect { Name = name, StatModifiers = { ["Speed"] = speed } });
        return c;
    }

    private static async Task<List<string>> Travel(params Character[] party)
    {
        var handler = new TravelChangeHandler(new EncounterResolver(() => 1.0));
        var dispatcher = new WorldChangeDispatcher(
            [handler, new SpyHandler<EventOccurred>(), new SpyHandler<ActivityChange>(), new SpyHandler<NeedChange>()],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance);
        var gate = new Location
        {
            Id = "locations/gate", Name = "Gate", Exits = [new LocationExit("locations/street", "in", null, 2.0)],
        };
        var street = new Location { Id = "locations/street", Name = "Street" };
        var summary = new List<string>();
        var batch = party.Select(p => (WorldChange)new TravelChange { CharacterId = p.Id, DestinationLocationId = street.Id }).ToList();
        var context = ChangeContextTestHelper.Create(
            characters: party.ToDictionary(p => p.Id),
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location> { [gate.Id] = gate, [street.Id] = street },
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: summary,
            dispatcher: dispatcher,
            activeCombat: null);
        context.Batch = batch;
        foreach (var change in batch)
        {
            var result = await handler.ApplyAsync(change, context, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Message);
        }

        return summary;
    }

    [Fact]
    public async Task A_hobbled_traveller_stretches_the_trip_by_base_over_current_speed()
    {
        var summary = await Travel(Traveler("chars/a", 30, ("Hobbled", -20))); // 30 -> 10: three times as long

        Assert.Contains(summary, s => s.Contains("the trip takes 6h instead of 2h"));
    }

    [Fact]
    public async Task The_group_moves_at_its_slowest_members_pace_and_says_so_once()
    {
        var summary = await Travel(
            Traveler("chars/a", 30),
            Traveler("chars/b", 30, ("Numb legs", -10))); // 30 -> 20: 1.5x

        Assert.Single(summary, s => s.Contains("slows the way"));
        Assert.Contains(summary, s => s.Contains("chars/b slows the way (20 ft a round instead of 30)") && s.Contains("3h instead of 2h"));
    }

    [Fact]
    public async Task Nothing_changes_for_an_ordinary_party_or_a_haste_or_unknown_movement()
    {
        Assert.DoesNotContain(await Travel(Traveler("chars/a", 30)), s => s.Contains("slows the way"));
        Assert.DoesNotContain(await Travel(Traveler("chars/a", 30, ("Haste", 30))), s => s.Contains("slows the way"));
        Assert.DoesNotContain(await Travel(Traveler("chars/a", null, ("Hobbled", -20))), s => s.Contains("slows the way"));
    }

    [Fact]
    public async Task Armour_is_a_permanent_state_and_does_not_stretch_travel()
    {
        var plate = Traveler("chars/a", 30);
        plate.SystemStats.MovementModifier = -10;

        Assert.DoesNotContain(await Travel(plate), s => s.Contains("slows the way"));
    }

    [Fact]
    public async Task The_stretch_is_capped_at_three_times()
    {
        var summary = await Travel(Traveler("chars/a", 30, ("Crippled", -29))); // floor 5 ft: 6x, capped at 3x

        Assert.Contains(summary, s => s.Contains("6h instead of 2h"));
    }

    [Fact]
    public void The_card_and_view_show_effective_speed_and_why()
    {
        var c = Traveler("chars/a", 30, ("Hobbled", -20));
        Assert.Equal("10 ft (30 normally: Hobbled)", SpeedRules.Describe(c.SystemStats));
        Assert.Equal("30 ft", SpeedRules.Describe(Traveler("chars/b", 30).SystemStats));
        Assert.Null(SpeedRules.Describe(Traveler("chars/c", null).SystemStats));
        Assert.Equal("10 ft (30 normally: Hobbled)", NpcStatLine.From(c.SystemStats)!.Speed);
        Assert.Equal("10 ft (30 normally: Hobbled)", CharacterDetailView.From(c).Speed);
        Assert.Null(CharacterDetailView.From(c, includeCombatDetail: false).Speed);
    }
}
