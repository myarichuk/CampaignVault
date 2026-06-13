using CampaignVault.Middleware;
using Xunit;

namespace CampaignVault.Tests;

public class McpToolErrorFilterTests
{
    [Theory]
    [InlineData("select_campaign", "campaignName", "list_campaigns")]
    [InlineData("commit", "changes", "get_help")]
    [InlineData("get_scene", "locationId", "search_world")]
    public void BuildMissingParamMessage_IncludesToolSpecificHints(string tool, string param, string expectedHint)
    {
        var message = McpToolErrorFilter.BuildMissingParamMessage(tool, param);

        Assert.Contains(param, message);
        Assert.Contains(expectedHint, message);
    }
}