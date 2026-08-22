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
    public void Enrich_NonEnumConversionFailure_ReturnsMessageUnchanged()
    {
        var raw = "The JSON value could not be converted to System.Nullable`1[System.Double]. " +
                   "Path: $.changes[13].salience | LineNumber: 0 | BytePositionInLine: 2900.";

        JsonException ex;
        try
        {
            throw new JsonException(raw, path: "$.changes[13].salience", lineNumber: 0, bytePositionInLine: 2900);
        }
        catch (JsonException caught)
        {
            ex = caught;
        }

        var enriched = ModelEnumErrorHints.Enrich(ex);

        Assert.Equal(raw, enriched);
    }
}
