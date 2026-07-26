using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests;

[Trait("Category", "Docker")]
public class GrpcSyncConnectionTests
{
    [Fact]
    public async Task TestConnection_WhenServerRunning_ReturnsSuccessOrActionableError()
    {
        var (success, message) = await VaultGrpcClientFactory.TestConnectionAsync("localhost", 50051);

        // When CampaignVault is running locally, this should connect.
        // In CI without a server, we still want a clear refusal message rather than a crash.
        if (success)
        {
            Assert.Contains("50051", message);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(message));
        }
    }
}
