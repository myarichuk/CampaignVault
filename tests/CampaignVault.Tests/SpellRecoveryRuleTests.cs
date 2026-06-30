using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class SpellRecoveryRuleTests
{
    private readonly SpellRecoveryRule _sut = new(NullLogger<SpellRecoveryRule>.Instance);

    [Fact]
    public async Task ApplyAsync_MultiDayAdvanceAfterRest_RecoversPools()
    {
        var character = CreateWizardWithDepletedSlots(lastRestedDay: 5, lastRestRecoveredDay: null);

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 10 },
            [],
            [character],
            null!,
            5,
            "test-camp");

        var result = await _sut.ApplyAsync(context, CancellationToken.None);

        var resourceDeltas = result.Deltas.OfType<ResourceChange>().ToList();
        Assert.NotEmpty(resourceDeltas);
        Assert.Contains(resourceDeltas, d => d.PoolName == "spell_slots_1" && d.Delta == 2);

        var ack = Assert.Single(result.Deltas.OfType<RestRecoveryAck>());
        Assert.Equal("chars/wizard", ack.CharacterId);
        Assert.Equal(5, ack.RestDay);
    }

    [Fact]
    public async Task ApplyAsync_AlreadyRecoveredRest_SkipsCharacter()
    {
        var character = CreateWizardWithDepletedSlots(lastRestedDay: 5, lastRestRecoveredDay: 5);

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 10 },
            [],
            [character],
            null!,
            5,
            "test-camp");

        var result = await _sut.ApplyAsync(context, CancellationToken.None);

        Assert.Empty(result.Deltas);
    }

    [Fact]
    public async Task ApplyAsync_NewRestAfterPriorRecovery_RecoversAgain()
    {
        var character = CreateWizardWithDepletedSlots(lastRestedDay: 12, lastRestRecoveredDay: 5);

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 15 },
            [],
            [character],
            null!,
            3,
            "test-camp");

        var result = await _sut.ApplyAsync(context, CancellationToken.None);

        Assert.NotEmpty(result.Deltas.OfType<ResourceChange>());
        var ack = Assert.Single(result.Deltas.OfType<RestRecoveryAck>());
        Assert.Equal(12, ack.RestDay);
    }

    [Fact]
    public async Task ApplyAsync_ShortRest_DoesNotRecoverLongRestPools()
    {
        var character = new Character
        {
            Id = "chars/wizard",
            Name = "Wizard",
            LastRestedDay = 5,
            LastRestType = RestType.ShortRest,
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 0, Max = 4, Recovery = RecoveryType.LongRest },
                    ["ki_points"] = new() { Current = 0, Max = 5, Recovery = RecoveryType.ShortRest }
                }
            }
        };

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 6 },
            [],
            [character],
            null!,
            1,
            "test-camp");

        var result = await _sut.ApplyAsync(context, CancellationToken.None);

        var resourceDeltas = result.Deltas.OfType<ResourceChange>().ToList();
        Assert.Single(resourceDeltas);
        Assert.Equal("ki_points", resourceDeltas[0].PoolName);
        Assert.Single(result.Deltas.OfType<RestRecoveryAck>());
    }

    private static Character CreateWizardWithDepletedSlots(int lastRestedDay, int? lastRestRecoveredDay) =>
        new()
        {
            Id = "chars/wizard",
            Name = "Wizard",
            LastRestedDay = lastRestedDay,
            LastRestType = RestType.LongRest,
            LastRestRecoveredDay = lastRestRecoveredDay,
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 2, Max = 4, Recovery = RecoveryType.LongRest }
                }
            }
        };
}