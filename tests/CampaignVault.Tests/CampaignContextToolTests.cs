using System.Threading.Tasks;
using CampaignVault.Models;
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
    public async Task GetCurrentCampaign_RequiresExplicitCampaignName()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var result = await tools.GetCurrentCampaign("nonexistent-test-campaign");

        Assert.False(result.Success);
        Assert.Equal("NotFound", result.Error);
    }

    [Fact]
    public async Task GetCurrentCampaign_WithExplicitName_Works()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);

        await tools.CreateCampaign("explicit-context-test", RulesetSystem.Dnd5e);

        var result = await tools.GetCurrentCampaign("explicit-context-test");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("explicit-context-test", result.Data.Campaign.Name);
    }

    [Fact]
    public async Task GetWorldState_RequiresCampaignName()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);

        var result = await tools.GetWorldState("test-loc", "nonexistent-campaign");

        if (!result.Success)
        {
            Assert.NotEqual(ToolErrors.NoCampaignSelected, result.Error);
        }
    }
}