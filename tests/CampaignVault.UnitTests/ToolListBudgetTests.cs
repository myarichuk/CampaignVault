using System.Linq;
using System.Text.Json;
using CampaignVault.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// tools/list is paid on every session before the first turn. Builds the tool collection the way
/// Program.cs does (WithToolsFromAssembly + AddCampaignVaultToolSchemas) and pins its size.
/// Session 1 audit: 51,853 → 21,973 chars (compact JSON, 18 tools, Stub mode). This serializer escapes
/// non-ASCII, so it reads ~22.6k for the same set.
/// </summary>
public class ToolListBudgetTests
{
    private const int ToolListCharBudget = 24_000;

    private static McpServerOptions BuildOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddMcpServer().WithToolsFromAssembly(typeof(McpSchemaInstaller).Assembly);
        services.AddCampaignVaultToolSchemas();
        return services.BuildServiceProvider().GetRequiredService<IOptions<McpServerOptions>>().Value;
    }

    [Fact]
    public void ToolsList_StaysWithinBudget()
    {
        var tools = BuildOptions().ToolCollection!.Select(t => t.ProtocolTool).ToList();
        var json = JsonSerializer.Serialize(tools, McpJsonUtilities.DefaultOptions);

        Assert.True(tools.Count >= 15, $"expected the full tool set, got {tools.Count}");
        Assert.True(json.Length <= ToolListCharBudget,
            $"tools/list grew to {json.Length} chars ({tools.Count} tools); budget {ToolListCharBudget}. " +
            string.Join(", ", tools.Select(t => $"{t.Name}={JsonSerializer.Serialize(t, McpJsonUtilities.DefaultOptions).Length}")));
    }

    [Fact]
    public void OutputSchemas_AreStubbed()
    {
        foreach (var tool in BuildOptions().ToolCollection!.Select(t => t.ProtocolTool))
        {
            if (tool.OutputSchema is { } schema)
            {
                Assert.Equal("{\"type\":\"object\"}", schema.GetRawText().Replace(" ", ""));
            }
        }
    }
}
