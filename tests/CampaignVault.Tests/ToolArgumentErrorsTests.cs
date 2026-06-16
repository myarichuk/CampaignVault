using CampaignVault.Models;
using CampaignVault.Tools;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class ToolArgumentErrorsTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ToolArgumentErrorsTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignTools CreateTools() => TestCampaignToolsFactory.Create(_fixture);

    [Fact]
    public async Task SelectCampaign_MissingName_ReturnsFriendlyError()
    {
        var result = await CreateTools().SelectCampaign(null);

        Assert.False(result.Success);
        Assert.Equal("InvalidArgument", result.Error);
        Assert.Contains("campaignName", result.Summary);
        Assert.Contains("list_campaigns", result.Summary);
        Assert.Contains("select_campaign", result.Summary);
    }

    [Fact]
    public async Task Commit_MissingChanges_ReturnsFriendlyError()
    {
        WorldChange[]? noChanges = null;
        var result = await CreateTools().Commit(noChanges, narrative: "Something happened");

        Assert.False(result.Success);
        Assert.Equal("InvalidArgument", result.Error);
        Assert.Contains("changes", result.Summary);
        Assert.Contains("$type", result.Summary);
        Assert.Contains("get_help", result.Summary);
        Assert.NotNull(result.RetryExample);
        Assert.Equal("commit", result.RetryExample!.Value.GetProperty("params").GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetNpcContext_MissingCharacterId_ReturnsRetryExample()
    {
        var result = await CreateTools().GetNpcContext(null);

        Assert.False(result.Success);
        Assert.Contains("characterId", result.Summary);
        Assert.Contains("tools/call", result.Summary);
        Assert.NotNull(result.RetryExample);
        Assert.True(result.RetryExample!.Value.GetProperty("params").GetProperty("arguments").TryGetProperty("characterId", out _));
    }

    [Fact]
    public async Task Commit_MissingNarrative_ReturnsFriendlyError()
    {
        var changes = new WorldChange[] { new HpChange { CharacterId = "characters/hero", Delta = -1 } };
        var result = await CreateTools().Commit(changes, narrative: null);
        Assert.False(result.Success);
        Assert.Equal("InvalidArgument", result.Error);
        Assert.Contains("narrative", result.Summary);
    }
}