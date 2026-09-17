using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ScheduleEvaluationRuleTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ScheduleEvaluationRuleTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Regression guard: a recruited companion must not be silently relocated by their own Schedule —
    /// once recruited, their location is party-directed (explicit commits), not an ambient routine.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_SkipsPartyCompanion_EvenWithDivergentSchedule()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var companion = new Character
        {
            Id = "chars/companion-schedule-test",
            Name = "Kael",
            IsPartyCompanion = true,
            KeepAlive = true,
            CurrentLocationId = "locations/tavern",
            Schedule = new Schedule { DefaultLocationId = "locations/kael-home", Routines = [] }
        };

        var rule = new ScheduleEvaluationRule();
        var time = new CampaignTime { TotalDaysElapsed = 3, Hour = 10 };
        var ctx = new SimulationContext(time, [], [companion], session, 1, "companion-schedule-test");

        var result = await rule.ApplyAsync(ctx);

        Assert.Empty(result.Deltas);
        Assert.DoesNotContain(result.NarrativeEvents, n => n.Contains(companion.Name));
    }

    /// <summary>
    /// A KeepAlive, non-companion NPC relocated by schedule evaluation must leave a RecentlyDeparted
    /// breadcrumb on the origin location — the "Kael vanished from the tavern with no trace" bug.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_RecordsDeparture_ForKeepAliveNpcThatRelocates()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var npc = new Character
        {
            Id = "chars/keepalive-schedule-test",
            Name = "Observed Npc",
            IsPartyCompanion = false,
            KeepAlive = true,
            CurrentLocationId = "locations/tavern",
            Schedule = new Schedule { DefaultLocationId = "locations/npc-home", Routines = [] }
        };

        var rule = new ScheduleEvaluationRule();
        var time = new CampaignTime { TotalDaysElapsed = 3, Hour = 10 };
        var ctx = new SimulationContext(time, [], [npc], session, 1, "keepalive-schedule-test");

        var result = await rule.ApplyAsync(ctx);

        var activity = Assert.Single(result.Deltas.OfType<ActivityChange>());
        Assert.Equal("locations/npc-home", activity.NewLocationId);

        var locationUpdate = Assert.Single(result.Deltas.OfType<LocationUpdate>());
        Assert.Equal("locations/tavern", locationUpdate.LocationId);
        Assert.NotNull(locationUpdate.RecordDeparture);
        Assert.Equal(npc.Id, locationUpdate.RecordDeparture!.CharacterId);
        Assert.Equal(3, locationUpdate.RecordDeparture.DepartedAtDay);
    }

    /// <summary>
    /// A non-KeepAlive, non-companion (anonymous background) NPC still follows its schedule, but does NOT
    /// get the RecordDeparture breadcrumb — that's reserved for NPCs the DM already flagged as significant,
    /// so a populated city's background routine churn doesn't flood every location's RecentlyDeparted list.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_DoesNotRecordDeparture_ForNonKeepAliveNpc()
    {
        using var session = _fixture.Store.OpenAsyncSession();

        var npc = new Character
        {
            Id = "chars/background-schedule-test",
            Name = "Background Npc",
            IsPartyCompanion = false,
            KeepAlive = false,
            CurrentLocationId = "locations/tavern",
            Schedule = new Schedule { DefaultLocationId = "locations/npc-home", Routines = [] }
        };

        var rule = new ScheduleEvaluationRule();
        var time = new CampaignTime { TotalDaysElapsed = 3, Hour = 10 };
        var ctx = new SimulationContext(time, [], [npc], session, 1, "background-schedule-test");

        var result = await rule.ApplyAsync(ctx);

        Assert.Single(result.Deltas.OfType<ActivityChange>());
        Assert.Empty(result.Deltas.OfType<LocationUpdate>());
    }
}
