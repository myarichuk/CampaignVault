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

        Assert.Contains("take_turn", index, StringComparison.Ordinal);
        Assert.Contains("start_session", index, StringComparison.Ordinal);
        Assert.Contains("get_entity", index, StringComparison.Ordinal);
        Assert.Contains("combat", index, StringComparison.Ordinal);
        Assert.Contains("get_rules_reference", index, StringComparison.Ordinal);
        Assert.Contains("### Session & exploration", index, StringComparison.Ordinal);
    }
}
