using Autofac;
using CampaignVault.Data;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignRepositoryResolveTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CampaignRepositoryResolveTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetCampaignConfig_WithoutSelectionOrName_Throws()
    {
        var repo = _fixture.CreateRepository();

        using var session = _fixture.Store.OpenAsyncSession();

        await Assert.ThrowsAsync<CampaignNotSelectedException>(() => repo.GetCampaignConfigAsync(session));
    }
}
