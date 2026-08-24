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
        var cleaned = McpResponseCleaner.Clean(element);
        return cleaned == null ? element : JsonSerializer.SerializeToElement(cleaned);
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

    /// <summary>
    /// Empty-container hygiene: an empty array/object property is pure repeated boilerplate (every
    /// response DTO here defaults collections to non-null-but-empty rather than null), so it should be
    /// dropped — but scalars (including falsy ones: 0, false, "", null) and non-empty containers must
    /// survive untouched. Also covers the bottom-up cascade: an object whose only property was a
    /// vector field becomes empty only after stripping, and must still be caught in the same pass.
    /// </summary>
    [Fact]
    public void StripsEmptyContainers_ButKeepsScalarsAndNonEmptyContainers()
    {
        var input = JsonDocument.Parse(
            """
            {
              "id": "chars/a",
              "name": "Aria",
              "emptyArray": [],
              "emptyObject": {},
              "nonEmptyArray": ["x"],
              "nonEmptyObject": {"k": "v"},
              "zero": 0,
              "falseFlag": false,
              "emptyString": "",
              "nullValue": null,
              "cascadesToEmpty": {"semanticVector": [0.1, 0.2]},
              "nestedArrayWithEmptyChild": [{"id": "y", "tags": []}]
            }
            """).RootElement;

        var output = StripVectors(input);

        Assert.False(output.TryGetProperty("emptyArray", out _));
        Assert.False(output.TryGetProperty("emptyObject", out _));
        Assert.False(output.TryGetProperty("cascadesToEmpty", out _));

        Assert.True(output.TryGetProperty("nonEmptyArray", out _));
        Assert.True(output.TryGetProperty("nonEmptyObject", out _));
        Assert.True(output.TryGetProperty("zero", out var zero));
        Assert.Equal(0, zero.GetInt32());
        Assert.True(output.TryGetProperty("falseFlag", out var flag));
        Assert.False(flag.GetBoolean());
        Assert.True(output.TryGetProperty("emptyString", out var str));
        Assert.Equal("", str.GetString());
        // Explicit nulls go the same way as empty containers: "field":null says nothing that an absent
        // key doesn't, and the delta-mode trims null out roughly a dozen location fields per scene
        // precisely because the client already has them. Present-but-falsy scalars ("", 0, false) stay —
        // those are readings, and "HP 0" must stay distinguishable from "HP not reported".
        Assert.False(output.TryGetProperty("nullValue", out _));

        // The array ITEM itself is never dropped for being empty/near-empty after stripping — that would
        // shift array semantics (a list entry silently disappearing) — but recursion still cleans empty
        // PROPERTIES within it, same as any other object.
        var nestedItem = output.GetProperty("nestedArrayWithEmptyChild")[0];
        Assert.Equal("y", nestedItem.GetProperty("id").GetString());
        Assert.False(nestedItem.TryGetProperty("tags", out _));
    }

    /// <summary>
    /// Runs the real filter body over the result. Cleaning and Content-sync are one operation now
    /// (they always were on the wire), so the `cleaned` argument the old private-method seam took is
    /// gone: callers set StructuredContent and let Apply do both halves.
    /// </summary>
    private static void SyncContent(CallToolResult result) => McpResponseCleaner.Apply(result);

    /// <summary>
    /// Content must carry the full, cleaned payload — most MCP hosts (e.g. opencode) only ever
    /// forward Content into the model's context, never StructuredContent, so a summary-only
    /// Content block silently starves the model of the data it just asked for.
    /// </summary>
    [Fact]
    public void SyncsContentToFullCleanedData_NotJustSummary()
    {
        var structured = JsonDocument.Parse(
            """{"success":true,"summary":"World updated with 2 changes.","data":{"committed":true,"npcs":[{"name":"Old Owen"}]}}"""
        ).RootElement;
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "stale SDK dump" }],
            StructuredContent = structured,
        };

        SyncContent(result);

        var block = Assert.Single(result.Content);
        var text = Assert.IsType<TextContentBlock>(block);
        Assert.Contains("World updated with 2 changes.", text.Text);
        Assert.Contains("Old Owen", text.Text);
        Assert.Contains("committed", text.Text);
    }

    [Fact]
    public void SyncedContent_ContainsNoVectorFields()
    {
        var raw = JsonDocument.Parse(
            """{"id":"chars/eli-harlan","name":"Eli","semanticVector":[0.1,0.2,0.3],"embeddingTextHash":"ABC123"}"""
        ).RootElement;
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "stale SDK dump" }],
            StructuredContent = raw,
        };

        SyncContent(result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.DoesNotContain("semanticVector", text.Text);
        Assert.DoesNotContain("embeddingTextHash", text.Text);
        Assert.Contains("Eli", text.Text);
    }

    [Fact]
    public void SyncedContent_IsCompact_NoIndentationWhitespace()
    {
        var structured = JsonDocument.Parse(
            """{"success":true,"summary":"ok","data":{"a":1,"b":2}}"""
        ).RootElement;
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "stale SDK dump" }],
            StructuredContent = structured,
        };

        SyncContent(result);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.DoesNotContain("\n", text.Text);
        Assert.DoesNotContain("  ", text.Text);
    }
}
