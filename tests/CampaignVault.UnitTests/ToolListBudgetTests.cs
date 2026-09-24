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
/// "/play" 16.3k, "/build" 11.9k in this serializer. With the /play world_build stub and prose trims,
/// live: "/" 19.5k, "/play" 12.5k, "/build" 11.4k.
/// </summary>
public class ToolListBudgetTests
{
    private const int ToolListCharBudget = 21_000;
    private const int PlayCharBudget = 13_000;
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

    // A stub that still ships $defs or an anyOf over every $type is a renamed dump, not a stub.
    [Fact]
    public void PlayTools_AreRealStubs()
    {
        var play = ToolProfiles.Filter(BuildOptions().ToolCollection, ToolProfile.Play)!;

        foreach (var tool in play.Select(t => t.ProtocolTool))
        {
            Assert.False(tool.InputSchema.TryGetProperty("$defs", out _), $"{tool.Name} on /play still has $defs");
        }

        var takeTurn = play.First(t => t.ProtocolTool.Name == "take_turn").ProtocolTool.InputSchema;
        var changeItem = takeTurn.GetProperty("properties").GetProperty("request")
            .GetProperty("properties").GetProperty("changes").GetProperty("items");
        Assert.False(changeItem.TryGetProperty("anyOf", out _));
        Assert.False(changeItem.TryGetProperty("oneOf", out _));
        Assert.Equal(["$type"], changeItem.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void PlayWorldBuild_IsSlim_WithoutChangingTheSharedTool()
    {
        var all = BuildOptions().ToolCollection!;
        var shared = all.First(t => t.ProtocolTool.Name == "world_build");
        var slim = ToolProfiles.Filter(all, ToolProfile.Play)!.First(t => t.ProtocolTool.Name == "world_build");
        var build = ToolProfiles.Filter(all, ToolProfile.Build)!.First(t => t.ProtocolTool.Name == "world_build");

        Assert.NotSame(shared, slim);
        Assert.Same(shared, build);
        Assert.True(shared.ProtocolTool.InputSchema.TryGetProperty("$defs", out _));
        Assert.True(JsonSerializer.Serialize(slim.ProtocolTool, McpJsonUtilities.DefaultOptions).Length < 1_200);
        Assert.Same(slim, ToolProfiles.Filter(all, ToolProfile.Play)!.First(t => t.ProtocolTool.Name == "world_build"));
    }

    [Theory]
    [InlineData("create_campaign", ToolProfile.Play, "unknown tool 'create_campaign' on /play; it is on /build (campaign setup).")]
    [InlineData("take_turn", ToolProfile.Build, "unknown tool 'take_turn' on /build; it is on /play (live play).")]
    [InlineData("get_rules_reference", ToolProfile.Play, "unknown tool 'get_rules_reference' on /play.")]
    public void UnknownToolMessage_IsOneDirectionalLine(string tool, ToolProfile profile, string expectedStart)
    {
        var available = ToolProfiles.Filter(BuildOptions().ToolCollection, profile)!
            .Select(t => t.ProtocolTool.Name).ToList();

        var message = ToolProfiles.UnknownToolMessage(tool, available);

        Assert.StartsWith(expectedStart, message);
        Assert.Contains("don't search for tools", message);
        Assert.DoesNotContain('\n', message);
        // Only this connector's tools, never the other profile's
        var other = profile == ToolProfile.Play ? "start_campaign_onboarding" : "recall_history";
        Assert.DoesNotContain(other, message);
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
