using System.Text.Json;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class ModelEnumErrorHintsTests
{
    [Fact]
    public void Enrich_NullableEnumConversionFailure_ListsValidValues()
    {
        // Reproduces the exact Grok Web take_turn failure: a bad `source` value on
        // knowledge_update produced this raw message from System.Text.Json, and the
        // enum-type regex previously choked on the "System.Nullable`1[...]" wrapper.
        var raw =
            "The JSON value could not be converted to System.Nullable`1[CampaignVault.Models.MemorySource]. " +
            "Path: $.changes[13].source | LineNumber: 0 | BytePositionInLine: 2932.";

        JsonException ex;
        try
        {
            throw new JsonException(raw, path: "$.changes[13].source", lineNumber: 0, bytePositionInLine: 2932);
        }
        catch (JsonException caught)
        {
            ex = caught;
        }

        var enriched = ModelEnumErrorHints.Enrich(ex);

        Assert.Contains("Valid values for MemorySource:", enriched);
        Assert.Contains("Witnessed", enriched);
        Assert.Contains("Heard", enriched);
        Assert.Contains("Told", enriched);
        Assert.Contains("Experienced", enriched);
        Assert.Contains("Trauma", enriched);
        Assert.Contains("Conditioned", enriched);
    }

    [Fact]
    public void Enrich_NumericConversionFailure_HintsExpectedNumber()
    {
        // Reproduces the companion Grok Web failure: `salience` sent as a word (e.g. "High")
        // instead of a 0.0-1.0 number. Not an enum, so there's no valid-values list — but the
        // raw CLR message alone doesn't tell the model what shape is expected either.
        var raw = "The JSON value could not be converted to System.Nullable`1[System.Double]. " +
                   "Path: $.changes[13].salience | LineNumber: 0 | BytePositionInLine: 3024.";

        JsonException ex;
        try
        {
            throw new JsonException(raw, path: "$.changes[13].salience", lineNumber: 0, bytePositionInLine: 3024);
        }
        catch (JsonException caught)
        {
            ex = caught;
        }

        var enriched = ModelEnumErrorHints.Enrich(ex);

        Assert.Contains("Expected a JSON number", enriched);
        Assert.StartsWith(raw, enriched);
    }

    [Fact]
    public void Enrich_UnresolvedTypeName_ReturnsMessageUnchanged()
    {
        // A conversion failure to some type we don't recognize as either an enum or a
        // numeric primitive (e.g. a nested object/DateTime) should pass through untouched
        // rather than attach a misleading hint.
        var raw = "The JSON value could not be converted to System.DateTime. " +
                   "Path: $.changes[2].occurredAt | LineNumber: 0 | BytePositionInLine: 120.";

        JsonException ex;
        try
        {
            throw new JsonException(raw, path: "$.changes[2].occurredAt", lineNumber: 0, bytePositionInLine: 120);
        }
        catch (JsonException caught)
        {
            ex = caught;
        }

        var enriched = ModelEnumErrorHints.Enrich(ex);

        Assert.Equal(raw, enriched);
    }
}
