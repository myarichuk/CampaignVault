using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("McpDescriptorSync")]
public class McpDescriptorSyncTests
{
    private static string DescriptorsDirectory =>
        Path.GetFullPath(Path.Combine(FindRepoRoot(), "mcps", "campaign-vault", "tools"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "mcps", "campaign-vault", "tools")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate mcps/campaign-vault/tools directory.");
    }

    [Fact]
    public void OnDiskDescriptors_MatchSourceMetadata()
    {
        RegenerateIfRequested();

        var built = McpDescriptorBuilder.BuildAll();
        Assert.NotEmpty(built);

        foreach (var (name, generatedJson) in built)
        {
            var path = Path.Combine(DescriptorsDirectory, $"{name}.json");
            Assert.True(File.Exists(path), $"Missing MCP descriptor file for tool '{name}' at {path}");

            var onDisk = File.ReadAllText(path);
            var expected = name.StartsWith("upsert_", StringComparison.Ordinal)
                ? McpDescriptorBuilder.MergePreservingNestedSchemas(onDisk, generatedJson)
                : generatedJson;

            if (!JsonSemanticEquals(onDisk, expected))
            {
                Assert.Fail($"MCP descriptor out of sync for '{name}'.\n\nExpected (memory):\n{expected}\n\nActual (disk):\n{onDisk}\n\nRegenerate with: dotnet test --filter RegenerateMcpDescriptors");
            }
        }
    }

    [Fact]
    public void RegenerateMcpDescriptors()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("REGENERATE_MCP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        RegenerateIfRequested();
    }

    private static void RegenerateIfRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("REGENERATE_MCP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(DescriptorsDirectory);
        foreach (var (name, generatedJson) in McpDescriptorBuilder.BuildAll())
        {
            var path = Path.Combine(DescriptorsDirectory, $"{name}.json");
            var output = name.StartsWith("upsert_", StringComparison.Ordinal) && File.Exists(path)
                ? McpDescriptorBuilder.MergePreservingNestedSchemas(File.ReadAllText(path), generatedJson)
                : generatedJson;
            File.WriteAllText(path, output);
        }
    }

    [Theory]
    [InlineData("upsert_item", "item")]
    [InlineData("upsert_creature", "creature")]
    [InlineData("upsert_plot_thread", "plotThread")]
    [InlineData("upsert_spell", "spell")]
    [InlineData("upsert_feat", "feat")]
    [InlineData("upsert_faction", "faction")]
    [InlineData("upsert_quest", "quest")]
    [InlineData("upsert_rumor", "rumor")]
    public void UpsertTool_Descriptor_HasNestedFieldSchema_NotBareObject(string toolName, string paramName)
    {
        var path = Path.Combine(DescriptorsDirectory, $"{toolName}.json");
        var onDisk = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(onDisk);
        var nested = doc.RootElement.GetProperty("inputSchema").GetProperty("properties").GetProperty(paramName);

        Assert.True(nested.TryGetProperty("properties", out var nestedProps),
            $"{toolName}'s '{paramName}' parameter has no nested field schema (still a bare object).");
        Assert.True(nestedProps.EnumerateObject().Any(),
            $"{toolName}'s '{paramName}' nested schema has no properties.");
    }

    [Fact]
    public void Commit_Descriptor_RequiresChangesAndNarrative()
    {
        var built = McpDescriptorBuilder.BuildAll();
        var commitJson = built["commit"];

        using var doc = JsonDocument.Parse(commitJson);
        var required = doc.RootElement.GetProperty("inputSchema").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("campaignName", required);
        Assert.Contains("changes", required);
        Assert.Contains("narrative", required);
    }

    private static bool JsonSemanticEquals(string left, string right)
    {
        left = left.Replace("\r\n", "\n");
        right = right.Replace("\r\n", "\n");
        var leftNode = JsonSerializer.SerializeToNode(JsonDocument.Parse(left).RootElement);
        var rightNode = JsonSerializer.SerializeToNode(JsonDocument.Parse(right).RootElement);
        return JsonSerializer.Serialize(leftNode) == JsonSerializer.Serialize(rightNode);
    }
}