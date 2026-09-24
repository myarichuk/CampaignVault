using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Guidance;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PluginGuidanceTests
{
    private const string TestAssembly = "CampaignVault.UnitTests";

    private static PressureContext Ctx(IReadOnlyList<WorldChange>? applied = null) =>
        new(CampaignName: "c", Time: new CampaignTime(), Config: new CampaignConfig(), Session: null!,
            PartyCharacterIds: ["chars/a"], AppliedChanges: applied);

    private static GuidanceOrchestrator Orchestrator(
        IEnumerable<IGuidanceContributor>? core = null,
        params IPluginGuidanceContributor[] plugins) =>
        new(core ?? [], plugins, new CampaignDocumentKeys());

    [Fact]
    public async Task PluginHint_IsStampedWithSourceAndNamespacedKey()
    {
        var hints = await Orchestrator(plugins: new FakePlugin(new PluginGuidanceHint("mode.enter", "Use crafting_step.") { Example = "{}" }))
            .CollectAsync(PressureScope.Both, Ctx(), ct: TestContext.Current.CancellationToken);

        var hint = Assert.Single(hints);
        Assert.Equal($"plugin:{TestAssembly}:mode.enter", hint.Key);
        Assert.Equal(TestAssembly, hint.Source);
        Assert.Equal(GuidanceTrigger.Plugin, hint.Trigger);
        Assert.Equal("{}", hint.Example);
    }

    [Fact]
    public async Task AtMostOnePluginHint_AndItIsTheHighestPriority()
    {
        var hints = await Orchestrator(plugins:
            [
                new FakePlugin(new PluginGuidanceHint("low", "Low.", 1), new PluginGuidanceHint("high", "High.", 9)),
                new FakePlugin(new PluginGuidanceHint("mid", "Mid.", 5))
            ])
            .CollectAsync(PressureScope.Both, Ctx(), ct: TestContext.Current.CancellationToken);

        var hint = Assert.Single(hints);
        Assert.EndsWith(":high", hint.Key);
    }

    [Fact]
    public async Task CoreHint_KeepsASlot_WhenPluginsOutrankIt()
    {
        var core = new FakeCore(new GuidanceHint("core.low", "Core.", GuidanceTrigger.FirstCommit, Priority: 0));
        var hints = await Orchestrator([core],
                new FakePlugin(new PluginGuidanceHint("a", "A.", 9), new PluginGuidanceHint("b", "B.", 9)))
            .CollectAsync(PressureScope.Both, Ctx(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, hints.Count);
        Assert.Contains(hints, h => h.Key == "core.low");
        Assert.Single(hints, h => h.Source != null);
    }

    [Fact]
    public async Task ThrowingPlugin_IsSwallowed()
    {
        var core = new FakeCore(new GuidanceHint("core", "Core.", GuidanceTrigger.FirstCommit));
        var hints = await Orchestrator([core], new ThrowingPlugin())
            .CollectAsync(PressureScope.Both, Ctx(), ct: TestContext.Current.CancellationToken);

        Assert.Equal("core", Assert.Single(hints).Key);
    }

    [Fact]
    public async Task PluginContext_ExposesAppliedChanges()
    {
        var plugin = new ModeEntryPlugin();
        var applied = new WorldChange[] { new ModeTransitionChange { ModeId = "crafting", Action = "enter" } };

        var hints = await Orchestrator(plugins: plugin)
            .CollectAsync(PressureScope.Both, Ctx(applied), ct: TestContext.Current.CancellationToken);
        var quiet = await Orchestrator(plugins: plugin)
            .CollectAsync(PressureScope.Both, Ctx(), ct: TestContext.Current.CancellationToken);

        Assert.Single(hints);
        Assert.Empty(quiet);
    }

    private sealed class FakePlugin(params PluginGuidanceHint[] hints) : IPluginGuidanceContributor
    {
        public Task<IEnumerable<PluginGuidanceHint>> EvaluateAsync(IGuidanceContext ctx, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<PluginGuidanceHint>>(hints);
    }

    private sealed class ThrowingPlugin : IPluginGuidanceContributor
    {
        public Task<IEnumerable<PluginGuidanceHint>> EvaluateAsync(IGuidanceContext ctx, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ModeEntryPlugin : IPluginGuidanceContributor
    {
        public Task<IEnumerable<PluginGuidanceHint>> EvaluateAsync(IGuidanceContext ctx, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<PluginGuidanceHint>>(
                ctx.AppliedChanges.OfType<ModeTransitionChange>().Any(m => m.ModeId == "crafting" && m.Action == "enter")
                    ? [new PluginGuidanceHint("crafting.enter", "Crafting started.")]
                    : []);
    }

    private sealed class FakeCore(params GuidanceHint[] hints) : IGuidanceContributor
    {
        public PressureScope Scope => PressureScope.Both;
        public int Order => 0;

        public Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<GuidanceHint>>(hints);
    }
}
