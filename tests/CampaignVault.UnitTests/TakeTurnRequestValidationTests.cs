using System;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// P1: take_turn input hardening — null request guard, whitespace narrative
/// rejection, whitespace ID normalization — plus ResourcePoolTemplate.StartsAt
/// validation (only omitted/max/zero accepted). Fixture-free: every case
/// returns before the repository/session is touched, so these run without
/// RavenDB (Docker is off in this environment).
/// </summary>
public class TakeTurnRequestValidationTests
{
    private static MutationTools CreateTools() =>
        new(null!, new CampaignDocumentKeys(), null!, null!, null!, null!, null!);

    [Fact]
    public async Task NullRequest_IsRejectedAsInvalidArgument()
    {
        var tools = CreateTools();

        var result = await tools.TakeTurn(null!, "some-campaign");

        Assert.False(result.Success);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error);
    }

    [Fact]
    public async Task WhitespaceNarrative_WithChanges_IsRejected()
    {
        var tools = CreateTools();

        var result = await tools.TakeTurn(
            new TakeTurnRequest
            {
                Changes = [new EventOccurred { Summary = "A beat." }],
                Narrative = "   ",
            },
            "some-campaign");

        Assert.False(result.Success);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error);
    }

    [Fact]
    public async Task WhitespaceOnlyRefreshIds_ReadAsAbsent()
    {
        var tools = CreateTools();

        var result = await tools.TakeTurn(
            new TakeTurnRequest
            {
                FullDetailCharacterId = "   ",
                FullDetailLocationId = "  ",
                MemoriesOnlyCharacterId = "\t",
                ExtraCharacterIds = ["  ", ""],
                ExtraLocationIds = [" "],
            },
            "some-campaign");

        Assert.False(result.Success);
        Assert.Equal(ToolErrors.InvalidArgument, result.Error);
        Assert.Contains("no Changes and no refresh parameters", result.Summary);
    }

    [Fact]
    public async Task PaddedNarrative_IsTrimmedBeforeValidation()
    {
        var tools = CreateTools();

        // Whitespace around a real narrative must not fail validation for the
        // wrong reason: with no refresh params the empty-call error (not the
        // narrative error) proves the narrative survived trimming.
        var request = new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "A beat." }],
            Narrative = "  A real beat.  ",
        };
        // Blank campaign name fails resolution before the repository is touched
        // (no RavenDB here), proving validation passed on the trimmed narrative.
        var result = await tools.TakeTurn(request, "   ");

        Assert.Equal("A real beat.", request.Narrative);
        Assert.Equal(ToolErrors.NoCampaignSelected, result.Error);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("max", "max")]
    [InlineData("MAX", "max")]
    [InlineData(" zero ", "zero")]
    [InlineData("Zero", "zero")]
    public void NormalizeStartsAt_AcceptsKnownValues(string? input, string? expected)
    {
        Assert.Equal(expected, ResourcePoolTemplate.NormalizeStartsAt(input));
    }

    [Theory]
    [InlineData("half")]
    [InlineData("full")]
    [InlineData("0")]
    public void NormalizeStartsAt_RejectsUnknownValues(string input)
    {
        Assert.Throws<ArgumentException>(() => ResourcePoolTemplate.NormalizeStartsAt(input));
    }

    [Fact]
    public void Merge_RejectsInvalidStartsAt()
    {
        var child = new ResourcePoolTemplate { StartsAt = "half" };
        var parent = new ResourcePoolTemplate();
        Assert.Throws<ArgumentException>(() => ResourcePoolTemplate.Merge(child, parent));
    }

    [Fact]
    public void Merge_NormalizesValidStartsAt()
    {
        var child = new ResourcePoolTemplate { StartsAt = " Zero " };
        var merged = ResourcePoolTemplate.Merge(child, new ResourcePoolTemplate());
        Assert.Equal("zero", merged.StartsAt);
    }
}
