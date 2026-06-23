using System;
using System.Text.Json;
using CampaignVault.Middleware;
using Xunit;

namespace CampaignVault.Tests;

public class McpToolErrorFilterTests
{
    [Theory]
    [InlineData("select_campaign", "campaignName", "list_campaigns")]
    [InlineData("commit", "changes", "get_help")]
    [InlineData("get_scene", "locationId", "search_world")]
    [InlineData("upsert_character", "character", "numeric-only")]
    public void BuildMissingParamMessage_IncludesToolSpecificHints(string tool, string param, string expectedHint)
    {
        var message = McpToolErrorFilter.BuildMissingParamMessage(tool, param);

        Assert.Contains(param, message);
        Assert.Contains(expectedHint, message);
    }

    [Fact]
    public void TryUnwrapJsonException_FindsDirectJsonException()
    {
        var ex = new JsonException("bad attributes");

        Assert.True(McpToolErrorFilter.TryUnwrapJsonException(ex, out var found));
        Assert.Same(ex, found);
    }

    [Fact]
    public void TryUnwrapJsonException_FindsWrappedInnerJsonException()
    {
        var inner = new JsonException("Path: $.systemStats.attributes.hitDie");
        var wrapped = new InvalidOperationException("Failed to bind tool arguments", inner);

        Assert.True(McpToolErrorFilter.TryUnwrapJsonException(wrapped, out var found));
        Assert.Same(inner, found);
    }

    [Fact]
    public void TryUnwrapJsonException_ReturnsFalse_ForUnrelatedExceptions()
    {
        Assert.False(McpToolErrorFilter.TryUnwrapJsonException(new InvalidOperationException("nope"), out _));
    }
}
