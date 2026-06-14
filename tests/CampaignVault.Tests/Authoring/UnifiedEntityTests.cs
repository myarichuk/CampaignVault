// tests/CampaignVault.Tests/Authoring/UnifiedEntityTests.cs
using Xunit;
using CampaignVault.Authoring.Models;

namespace CampaignVault.Tests.Authoring;

public class UnifiedEntityTests
{
    [Fact]
    public void UnifiedEntity_CalculatesSyncState()
    {
        var entity = new UnifiedEntity { LocalHash = "A", RemoteHash = "A", LastSyncedHash = "A" };
        Assert.Equal(SyncState.Synced, entity.CalculatedState);

        entity = new UnifiedEntity { LocalHash = "B", RemoteHash = "A", LastSyncedHash = "A" };
        Assert.Equal(SyncState.ModifiedLocally, entity.CalculatedState);
    }
}
