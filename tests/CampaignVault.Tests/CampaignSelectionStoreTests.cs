using System;
using CampaignVault.Data;
using Xunit;

namespace CampaignVault.Tests;

public class CampaignSelectionStoreTests
{
    [Fact]
    public void Sessions_AreIsolated()
    {
        var store = new CampaignSelectionStore();

        store.SetCurrent("session-a", "campaign-a");
        store.SetCurrent("session-b", "campaign-b");

        Assert.True(store.HasSelection("session-a"));
        Assert.True(store.HasSelection("session-b"));
        Assert.Equal("campaign-a", store.GetCurrent("session-a"));
        Assert.Equal("campaign-b", store.GetCurrent("session-b"));
        Assert.False(store.HasSelection("session-c"));
    }

    [Fact]
    public void NoSessionId_HasNoSelection()
    {
        var store = new CampaignSelectionStore();

        Assert.False(store.HasSelection(null));
        Assert.Equal(CampaignSelectionStore.UnselectedSentinel, store.GetCurrent(null));
    }

    [Fact]
    public void SetCurrent_WithoutSession_Throws()
    {
        var store = new CampaignSelectionStore();

        Assert.Throws<CampaignSessionRequiredException>(() => store.SetCurrent(null, "shared-campaign"));
    }

    [Fact]
    public void SetCurrent_CanonicalizesCampaignSlug()
    {
        var store = new CampaignSelectionStore();

        store.SetCurrent("session-a", "Dragon Heist");

        Assert.Equal("dragon-heist", store.GetCurrent("session-a"));
    }

    [Fact]
    public void SessionKeyedContext_ResolvesPerAccessor()
    {
        var store = new CampaignSelectionStore();
        var accessorA = new FixedMcpSessionAccessor("session-a");
        var accessorB = new FixedMcpSessionAccessor("session-b");

        var contextA = new SessionKeyedCurrentCampaignContext(store, accessorA);
        var contextB = new SessionKeyedCurrentCampaignContext(store, accessorB);

        contextA.SetCurrent("alpha");
        contextB.SetCurrent("beta");

        Assert.Equal("alpha", contextA.CurrentCampaignName);
        Assert.Equal("beta", contextB.CurrentCampaignName);
        Assert.True(contextA.HasSelection);
        Assert.True(contextB.HasSelection);
    }

    [Fact]
    public void PruneExpired_RemovesIdleSessionAfterConfiguredTimeout()
    {
        var time = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        var store = new CampaignSelectionStore(time, TimeSpan.FromHours(1));

        store.SetCurrent("session-a", "alpha");
        time.Advance(TimeSpan.FromHours(2));

        store.SetCurrent("session-b", "beta");

        Assert.False(store.HasSelection("session-a"));
        Assert.True(store.HasSelection("session-b"));
        Assert.Equal("beta", store.GetCurrent("session-b"));
    }

    private sealed class FixedMcpSessionAccessor(string sessionId) : IMcpSessionAccessor
    {
        public string? SessionId { get; } = sessionId;
    }

    private sealed class SteppedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
