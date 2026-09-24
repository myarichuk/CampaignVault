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
/// non-ASCII, so it reads ~22.6k for the same set. After the lookup merge (16 tools): "/" 21.1k,
/// "/play" 16.3k, "/build" 11.9k in this serializer.
/// </summary>
public class ToolListBudgetTests
{
    private const int ToolListCharBudget = 22_000;
    private const int PlayCharBudget = 17_000;
    private const int BuildCharBudget = 12_500;

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

    private static string Serialize(McpServerPrimitiveCollection<McpServerTool>? tools) =>
        JsonSerializer.Serialize(tools!.Select(t => t.ProtocolTool).ToList(), McpJsonUtilities.DefaultOptions);

    [Theory]
    [InlineData("/play", ToolProfile.Play, PlayCharBudget)]
    [InlineData("/build/", ToolProfile.Build, BuildCharBudget)]
    public void ProfileRoutes_ServeSmallerToolLists(string path, ToolProfile expected, int budget)
    {
        var all = BuildOptions().ToolCollection;
        var profile = ToolProfiles.FromPath(path);
        var json = Serialize(ToolProfiles.Filter(all, profile));

        Assert.Equal(expected, profile);
        Assert.True(json.Length <= budget, $"{path} tools/list is {json.Length} chars; budget {budget}.");
        Assert.True(json.Length < Serialize(all).Length);
    }

    [Fact]
    public void EveryTool_IsServedByPlayOrBuild_AndProfilesNameOnlyRealTools()
    {
        var names = BuildOptions().ToolCollection!.Select(t => t.ProtocolTool.Name).ToHashSet();

        Assert.DoesNotContain(names, n => !ToolProfiles.PlayTools.Contains(n) && !ToolProfiles.BuildTools.Contains(n));
        Assert.DoesNotContain(ToolProfiles.PlayTools.Concat(ToolProfiles.BuildTools), n => !names.Contains(n));
        Assert.Equal(ToolProfile.All, ToolProfiles.FromPath("/"));
        Assert.Null(ToolProfiles.Filter(null, ToolProfile.Play));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/play")]
    [InlineData("/build")]
    public void RetiredReferenceTools_AreGoneFromEveryProfile(string path)
    {
        var json = Serialize(ToolProfiles.Filter(BuildOptions().ToolCollection, ToolProfiles.FromPath(path)));

        Assert.DoesNotContain("\"get_rules_reference\"", json);
        Assert.DoesNotContain("\"get_commit_schema\"", json);
        Assert.DoesNotContain("\"get_help\"", json);
        Assert.Contains("\"lookup\"", json);
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
