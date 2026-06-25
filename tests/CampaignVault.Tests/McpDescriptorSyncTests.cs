using System;
using System.IO;
using System.Text.Json;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

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

    private static bool JsonSemanticEquals(string left, string right)
    {
        left = left.Replace("\r\n", "\n");
        right = right.Replace("\r\n", "\n");
        var leftNode = JsonSerializer.SerializeToNode(JsonDocument.Parse(left).RootElement);
        var rightNode = JsonSerializer.SerializeToNode(JsonDocument.Parse(right).RootElement);
        return JsonSerializer.Serialize(leftNode) == JsonSerializer.Serialize(rightNode);
    }
}