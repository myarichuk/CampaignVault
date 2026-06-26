using CampaignVault.Authoring.Vault;
using Xunit;

namespace CampaignVault.Tests.Authoring.Vault;

public sealed class VaultContentHashTests
{
    [Fact]
    public void Compute_NormalizesLineEndings()
    {
        var unix = "---\nid: test\n---\n";
        var windows = "---\r\nid: test\r\n---\r\n";

        Assert.Equal(VaultContentHash.Compute(unix), VaultContentHash.Compute(windows));
    }
}