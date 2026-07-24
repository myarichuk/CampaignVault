using System;
using System.Linq;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PressureAbbreviatorTests
{
    [Theory]
    [InlineData(PressureSeverity.Suggestion)]
    [InlineData(PressureSeverity.Simulation)]
    [InlineData(PressureSeverity.NarrativePrompt)]
    [InlineData(PressureSeverity.EngineWarning)]
    public void UnmatchedText_SurvivesToDisplayStrings_Verbatim(PressureSeverity severity)
    {
        // Guard against the lossy fallback bug: an unrecognized pressure text must never be
        // reduced to a generic code — the full message has to reach the LLM at every severity.
        const string text = "Consider granting the barkeep a moment of suspicion toward the hooded stranger.";
        var item = new WorldPressureItem(severity, "chars/barkeep", text, "Test:Unmatched");

        var abbreviated = item with { Abbreviation = PressureAbbreviator.TryAbbreviate(item) ?? item.Abbreviation };
        var display = PressureManager.ToDisplayStrings([abbreviated]);

        Assert.Single(display);
        Assert.Contains(text, display[0], StringComparison.Ordinal);
    }

    [Fact]
    public void HigherSeverities_AreNeverAbbreviated_EvenWhenPatternMatches()
    {
        var item = new WorldPressureItem(PressureSeverity.EngineWarning, "chars/valen",
            "This character is starving and needs food.", "Needs:Hunger");

        Assert.Null(PressureAbbreviator.TryAbbreviate(item));
    }

    [Fact]
    public void SuggestionWithRecognizedPattern_GetsTerseCode()
    {
        var item = new WorldPressureItem(PressureSeverity.Suggestion, "chars/valen",
            "This character is starving and needs food.", "Needs:Hunger");

        Assert.Equal("HUNGER", PressureAbbreviator.TryAbbreviate(item));
    }

    [Fact]
    public void QuestDeadlinePattern_IncludesTimeframe()
    {
        var item = new WorldPressureItem(PressureSeverity.Suggestion, "quests/rats_01",
            "Quest 'Clear the Cellar Rats' deadline in 3 days.", "Quest:Deadline");

        Assert.Equal("QUEST:deadline:3d", PressureAbbreviator.TryAbbreviate(item));
    }
}
