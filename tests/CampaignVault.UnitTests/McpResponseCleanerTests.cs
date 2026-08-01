using System.Reflection;
using System.Text.Json;
using CampaignVault.Middleware;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Regression test for the McpResponseCleaner casing bug: the strip set is built from
/// nameof(SemanticVector)/nameof(EmbeddingTextHash) (PascalCase), but the MCP SDK serializes
/// StructuredContent with a camelCase naming policy, so the real wire keys are
/// "semanticVector"/"embeddingTextHash". An ordinal comparer silently never matched them.
/// Exercises the actual stripping method (via reflection, since it's private) rather than
/// re-serializing with default PascalCase JsonSerializerOptions like the in-process tool
/// tests do — those can pass even when the filter is completely broken.
/// </summary>
public class McpResponseCleanerTests
{
    private static JsonElement StripVectors(JsonElement element)
    {
        var method = typeof(McpResponseCleaner).GetMethod(
            "StripVectorsFromElement",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (JsonElement)method!.Invoke(null, [element])!;
    }

    [Fact]
    public void StripsCamelCaseVectorFieldsFromObject()
    {
        var input = JsonDocument.Parse(
            """
            {"id":"chars/eli-harlan","name":"Eli","semanticVector":[0.1,0.2,0.3],"embeddingTextHash":"ABC123"}
            """).RootElement;

        var output = StripVectors(input);

        Assert.False(output.TryGetProperty("semanticVector", out _));
        Assert.False(output.TryGetProperty("embeddingTextHash", out _));
        Assert.True(output.TryGetProperty("id", out var idProp));
        Assert.Equal("chars/eli-harlan", idProp.GetString());
        Assert.True(output.TryGetProperty("name", out var nameProp));
        Assert.Equal("Eli", nameProp.GetString());
    }

    [Fact]
    public void StripsCamelCaseVectorFieldsFromNestedArraysAndObjects()
    {
        var input = JsonDocument.Parse(
            """
            {
              "matches": [
                {"id":"locations/forest","semanticVector":[0.4,0.5],"embeddingTextHash":"H1","name":"Forest"},
                {"id":"events/1","semanticVector":[0.6],"embeddingTextHash":"H2","summary":"Something happened"}
              ],
              "character": {"id":"chars/a","semanticVector":[0.7],"embeddingTextHash":"H3"}
            }
            """).RootElement;

        var output = StripVectors(input);

        foreach (var match in output.GetProperty("matches").EnumerateArray())
        {
            Assert.False(match.TryGetProperty("semanticVector", out _));
            Assert.False(match.TryGetProperty("embeddingTextHash", out _));
        }

        var character = output.GetProperty("character");
        Assert.False(character.TryGetProperty("semanticVector", out _));
        Assert.False(character.TryGetProperty("embeddingTextHash", out _));

        Assert.Equal("Forest", output.GetProperty("matches")[0].GetProperty("name").GetString());
        Assert.Equal("Something happened", output.GetProperty("matches")[1].GetProperty("summary").GetString());
    }

    private static void CollapseContentToSummary(CallToolResult result)
    {
        var method = typeof(McpResponseCleaner).GetMethod(
            "TryCollapseContentToSummary",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [result]);
    }

    [Fact]
    public void CollapsesContentToSummary_WhenStructuredContentHasSummary()
    {
        var structured = JsonDocument.Parse(
            """{"success":true,"summary":"World updated with 2 changes.","data":{"committed":true,"npcs":[{"name":"Old Owen"}]}}"""
        ).RootElement;
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(structured) }],
            StructuredContent = structured,
        };

        CollapseContentToSummary(result);

        var block = Assert.Single(result.Content);
        var text = Assert.IsType<TextContentBlock>(block);
        Assert.Equal("World updated with 2 changes.", text.Text);
    }

    [Fact]
    public void LeavesContentUntouched_WhenSummaryMissing()
    {
        var structured = JsonDocument.Parse("""{"success":true,"data":{"committed":true}}""").RootElement;
        var original = new TextContentBlock { Text = "original text" };
        var result = new CallToolResult
        {
            Content = [original],
            StructuredContent = structured,
        };

        CollapseContentToSummary(result);

        Assert.Same(original, Assert.Single(result.Content));
    }

    [Fact]
    public void LeavesContentUntouched_WhenSummaryIsEmpty()
    {
        var structured = JsonDocument.Parse("""{"success":true,"summary":"","data":{}}""").RootElement;
        var original = new TextContentBlock { Text = "original text" };
        var result = new CallToolResult
        {
            Content = [original],
            StructuredContent = structured,
        };

        CollapseContentToSummary(result);

        Assert.Same(original, Assert.Single(result.Content));
    }
}
