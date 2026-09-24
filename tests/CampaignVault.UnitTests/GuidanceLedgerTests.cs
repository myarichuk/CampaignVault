using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Guidance;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Guidance hints are one-shot: the orchestrator records what it delivered, and a new session
/// (start_session clears the ledger) teaches each once more.</summary>
[Collection("RavenDB")]
public class GuidanceLedgerTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public GuidanceLedgerTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    [Fact]
    public async Task Hint_IsDeliveredOnce_ThenAgainAfterTheLedgerIsCleared()
    {
        var campaign = "guidance-ledger-" + Guid.NewGuid().ToString("N")[..8];
        var keys = new CampaignDocumentKeys();
        var orchestrator = new GuidanceOrchestrator(
            [new OneHint()], Array.Empty<IPluginGuidanceContributor>(), keys);

        async Task<int> CollectAsync()
        {
            using var session = _store.OpenAsyncSession();
            var ctx = new PressureContext(CampaignName: campaign, Time: new CampaignTime(), Config: new CampaignConfig(),
                Session: session, PartyCharacterIds: ["chars/a"]);
            var hints = await orchestrator.CollectAsync(PressureScope.Both, ctx, ct: TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            return hints.Count;
        }

        Assert.Equal(1, await CollectAsync());
        Assert.Equal(0, await CollectAsync());

        using (var session = _store.OpenAsyncSession())
        {
            var ledger = await session.LoadAsync<GuidanceLedger>(keys.StateGuidance(campaign), TestContext.Current.CancellationToken);
            Assert.NotNull(ledger);
            ledger.Delivered.Clear();
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await CollectAsync());
    }

    private sealed class OneHint : IGuidanceContributor
    {
        public PressureScope Scope => PressureScope.Both;
        public int Order => 0;

        public Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default) =>
            Task.FromResult<IEnumerable<GuidanceHint>>([new GuidanceHint("travel.rest", "Rest after long travel.", GuidanceTrigger.FirstCommit)]);
    }
}
