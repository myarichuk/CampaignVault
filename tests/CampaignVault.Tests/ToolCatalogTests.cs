using System;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class ToolCatalogTests
{
    [Fact]
    public void FormatHelpIndex_IncludesCoreTools()
    {
        var index = ToolCatalog.FormatHelpIndex();

        Assert.Contains("get_party", index, StringComparison.Ordinal);
        Assert.Contains("select_campaign", index, StringComparison.Ordinal);
        Assert.Contains("list_tools", index, StringComparison.Ordinal);
        Assert.Contains("### Session & exploration", index, StringComparison.Ordinal);
    }
}