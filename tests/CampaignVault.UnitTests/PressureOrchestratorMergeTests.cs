using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public class PressureOrchestratorMergeTests
{
    private sealed class FakeContributor(WorldPressureItem item) : IPressureContributor
    {
        public PressureScope Scope => PressureScope.Both;
        public int Order => 0;
        public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<WorldPressureItem>>([item]);
    }

    private sealed class PassThroughPressureManager : IPressureManager
    {
        public Task<List<WorldPressureItem>> FilterAndCapAsync(IAsyncDocumentSession session, string campaignName, int currentDay,
            IEnumerable<WorldPressureItem> rawPressures, bool disableCooldowns = false) =>
            Task.FromResult(rawPressures.ToList());
    }

    [Fact]
    public async Task CollectAndCapAsync_SameGroupingKeyAndEntity_DifferentText_BothSurvive()
    {
        var itemA = new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is starving.", "Character:Morale");
        var itemB = new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is dehydrated.", "Character:Morale");

        var selector = new RulesetModuleSelector([
            new Dnd5eRulesetResolver(new DefaultRollService()),
            new Pf2eRulesetResolver(new DefaultRollService()),
            new NarrativeRulesetResolver(new DefaultRollService())
        ]);

        var orchestrator = new PressureOrchestrator(
            [new FakeContributor(itemA), new FakeContributor(itemB)],
            new PassThroughPressureManager(),
            selector);

        var ctx = new PressureContext("test-camp", new CampaignTime(), new CampaignConfig { ActiveSystem = RulesetSystem.Narrative }, null!);
        var result = await orchestrator.CollectAndCapAsync(PressureScope.World, ctx);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Text == "Character is starving.");
        Assert.Contains(result, p => p.Text == "Character is dehydrated.");
    }

    [Fact]
    public async Task CollectAndCapAsync_SameGroupingKeyEntityAndText_Deduplicates()
    {
        var itemA = new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is starving.", "Character:Morale");
        var itemB = new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is starving.", "Character:Morale");

        var selector = new RulesetModuleSelector([
            new Dnd5eRulesetResolver(new DefaultRollService()),
            new Pf2eRulesetResolver(new DefaultRollService()),
            new NarrativeRulesetResolver(new DefaultRollService())
        ]);

        var orchestrator = new PressureOrchestrator(
            [new FakeContributor(itemA), new FakeContributor(itemB)],
            new PassThroughPressureManager(),
            selector);

        var ctx = new PressureContext("test-camp", new CampaignTime(), new CampaignConfig { ActiveSystem = RulesetSystem.Narrative }, null!);
        var result = await orchestrator.CollectAndCapAsync(PressureScope.World, ctx);

        Assert.Single(result);
    }
}
