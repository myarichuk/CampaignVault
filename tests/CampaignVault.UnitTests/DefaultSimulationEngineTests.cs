using System;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class DefaultSimulationEngineTests
{
    private sealed class ThrowingRule : ISimulationRule
    {
        public string Name => "Throwing Rule";
        public int Order => 1;

        public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class WorkingRule : ISimulationRule
    {
        public string Name => "Working Rule";
        public int Order => 2;

        public Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
            => Task.FromResult(new RuleResult(["ok"], []));
    }

    [Fact]
    public async Task RunAsync_ContinuesAfterRuleFailure()
    {
        var engine = new DefaultSimulationEngine(
            [new ThrowingRule(), new WorkingRule()],
            NullLogger<DefaultSimulationEngine>.Instance);

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 1 },
            [],
            [],
            null!,
            DaysPassed: 1);

        var result = await engine.RunAsync(context);

        // Engine surfaces a narrative for the failed rule (so failures are visible, not silent)
        // AND continues on to run the remaining rules.
        Assert.Equal(2, result.NarrativeEvents.Count);
        Assert.Contains(result.NarrativeEvents, n => n.Contains("Throwing Rule"));
        Assert.Contains("ok", result.NarrativeEvents);
    }
}
