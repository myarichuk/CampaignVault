using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignContextToolTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CampaignContextToolTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetCurrentCampaign_WithoutSelection_ReturnsNoCampaignSelected()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture, new CurrentCampaignContext());

        var result = await tools.GetCurrentCampaign();

        Assert.False(result.Success);
        Assert.Equal(ToolErrors.NoCampaignSelected, result.Error);
    }

    [Fact]
    public async Task GetCurrentCampaign_WithExplicitName_DoesNotRequireSelection()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture, new CurrentCampaignContext());

        await tools.SelectCampaign("explicit-context-test");

        var result = await tools.GetCurrentCampaign("explicit-context-test");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("explicit-context-test", result.Data.Name);
    }

    [Fact]
    public async Task GetWorldState_WithoutSelection_ReturnsNoCampaignSelected()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture, new CurrentCampaignContext());

        var result = await tools.GetWorldState();

        Assert.False(result.Success);
        Assert.Equal(ToolErrors.NoCampaignSelected, result.Error);
    }
}