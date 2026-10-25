using System;
using System.Linq;
using System.Text.Json;
using CampaignVault.Models;
using CampaignVault.Models.Converters;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Phase 3a: unknown fields fail loudly and every binding error is short and names the fix.</summary>
public class CommitBindingErrorTests
{
    private static JsonElement Changes(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string ParseError(string json)
    {
        Assert.False(CommitChangesParser.TryParse(Changes(json), out _, out var error));
        Assert.NotNull(error);
        return error!;
    }

    [Fact]
    public void UnknownField_FailsTheBatch_AndNamesTheTypesFields()
    {
        var error = ParseError("""[{"$type":"hp","characterId":"chars/a","target":"chars/b","delta":-3}]""");

        Assert.Contains("Unknown field 'target'", error);
        Assert.Contains("$[0]", error);
        Assert.Contains("$type hp", error);
        Assert.Contains("Fields of hp:", error);
        Assert.Contains("characterId:string [req]", error);
        Assert.True(error.Length <= CommitBindingError.MaxChars, error);
    }

    [Fact]
    public void UnknownField_SuggestsTheNearestName()
    {
        var error = ParseError("""[{"$type":"engagement_relation","characterId":"chars/a","target":"chars/b","verb":"watching"}]""");
        Assert.Contains("Did you mean 'targetId'", error);
    }

    [Fact]
    public void UnknownType_ListsValidTypes()
    {
        var error = ParseError("""[{"$type":"heal","characterId":"chars/a"}]""");

        Assert.Contains("Unknown $type 'heal'", error);
        Assert.Contains("Valid $type values:", error);
        Assert.Contains("hp", error);
        Assert.True(error.Length <= CommitBindingError.MaxChars, error);
    }

    [Fact]
    public void MissingType_ThatCannotBeInferred_ListsValidTypes()
    {
        var error = ParseError("""[{"$type":"hp","characterId":"chars/a","delta":-1},{"foo":"bar"}]""");

        Assert.Contains("$[1] is missing $type", error);
        Assert.Contains("Valid $type values:", error);
        Assert.DoesNotContain("WorldChange's own description", error);
    }

    [Fact]
    public void MissingType_SurfacesAsJsonException_FromTheConverter()
    {
        var ex = Assert.Throws<JsonException>(() => WorldChangeNormalizer.NormalizeChangesArray("""[{"foo":"bar"}]"""));
        Assert.Contains(CommitBindingError.MissingTypeMarker, ex.Message);
    }

    [Fact]
    public void BadEnum_NamesTheValueAndTheValidList()
    {
        var error = ParseError("""[{"$type":"event","category":"Scene","summary":"x","involved":["chars/a"]}]""");

        Assert.Contains("Scene", error);
        Assert.Contains("Conversation", error);
        Assert.DoesNotContain("LineNumber", error);
        Assert.True(error.Length <= CommitBindingError.MaxChars, error);
    }

    [Fact]
    public void ErrorResponse_NoLongerCarriesTheHintBlob()
    {
        var (summary, retry) = ToolCallExamples.BuildDeserializationErrorResponse("take_turn", "Unknown field 'x' at $[0] ($type hp).");

        Assert.NotNull(retry);
        Assert.Contains("Unknown field 'x'", summary);
        Assert.DoesNotContain("CRITICAL", summary);
        Assert.DoesNotContain("jsonrpc", summary);
        Assert.True(summary.Length < 200, summary);
    }

    [Fact]
    public void Suggest_PrefersCaseThenContainmentThenEdits()
    {
        var fields = new[] { "characterId", "targetId", "verb", "category" };
        Assert.Equal("characterId", CommitBindingError.Suggest("characterid", fields));
        Assert.Equal("targetId", CommitBindingError.Suggest("target", fields));
        Assert.Equal("verb", CommitBindingError.Suggest("vreb", fields));
        Assert.Null(CommitBindingError.Suggest("completelyDifferent", fields));
    }

    [Fact]
    public void FieldReference_RendersTypeRequiredEnumAndShape()
    {
        var status = CommitSchemaModel.Find("status")!;
        var lines = CommitFieldReference.FieldLines(status);

        Assert.Contains(lines, l => l.StartsWith("characterId:string [req]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("effect:object {", StringComparison.Ordinal) && l.Contains("statModifiers:map<string,number>"));

        var evt = CommitSchemaModel.Find("event")!;
        Assert.Contains(CommitFieldReference.FieldLines(evt), l => l.StartsWith("category:enum (", StringComparison.Ordinal) && l.Contains("Conversation"));
    }

    [Fact]
    public void Registry_FieldStrings_UseTheSharedFormat()
    {
        var hp = CommitSchemaRegistry.GetAll(type: "hp").Single();
        Assert.Contains(hp.RequiredFields, f => f.StartsWith("characterId:string [req]", StringComparison.Ordinal));
        Assert.All(hp.OptionalFields, f => Assert.Contains(':', f));
    }
}
