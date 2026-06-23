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
    public void NullSessionId_UsesProcessFallback()
    {
        var store = new CampaignSelectionStore();

        store.SetCurrent(null, "shared-campaign");

        Assert.True(store.HasSelection(null));
        Assert.Equal("shared-campaign", store.GetCurrent(null));
        Assert.Equal("shared-campaign", store.GetCurrent("   "));
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

    private sealed class FixedMcpSessionAccessor(string sessionId) : IMcpSessionAccessor
    {
        public string? SessionId { get; } = sessionId;
    }
}