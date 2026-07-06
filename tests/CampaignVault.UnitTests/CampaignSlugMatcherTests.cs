using System.Collections.Generic;
using System.Linq;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class CampaignSlugMatcherTests
{
    [Fact]
    public void FindSuggestions_ReturnsCloseSlugMatch()
    {
        var campaigns = new List<Campaign>
        {
            new()
            {
                Id = "campaigns/sword-coast/meta",
                Name = "sword-coast",
                DisplayName = "Sword Coast",
                System = RulesetSystem.Dnd5e,
            }
        };

        var suggestions = CampaignSlugMatcher.FindSuggestions(
            "swordcoast",
            campaigns,
            c => new CampaignSuggestion(c.Name, c.DisplayName, c.System, 0, null));

        Assert.Single(suggestions);
        Assert.Equal("sword-coast", suggestions[0].Slug);
    }

    [Fact]
    public void FindSuggestions_ExcludesExactMatch()
    {
        var campaigns = new List<Campaign>
        {
            new()
            {
                Id = "campaigns/sword-coast/meta",
                Name = "sword-coast",
                DisplayName = "Sword Coast",
                System = RulesetSystem.Dnd5e,
            }
        };

        var suggestions = CampaignSlugMatcher.FindSuggestions(
            "sword-coast",
            campaigns,
            c => new CampaignSuggestion(c.Name, c.DisplayName, c.System, 0, null));

        Assert.Empty(suggestions);
    }

    [Fact]
    public void FindSuggestions_MatchesHyphenStrippedSlugWithSuffix()
    {
        var suffix = "a1b2c3";
        var campaigns = new List<Campaign>
        {
            new()
            {
                Id = $"campaigns/sword-coast-{suffix}/meta",
                Name = $"sword-coast-{suffix}",
                DisplayName = "Sword Coast",
                System = RulesetSystem.Dnd5e,
            }
        };

        var suggestions = CampaignSlugMatcher.FindSuggestions(
            $"swordcoast{suffix}",
            campaigns,
            c => new CampaignSuggestion(c.Name, c.DisplayName, c.System, 0, null));

        Assert.Single(suggestions);
        Assert.Equal($"sword-coast-{suffix}", suggestions[0].Slug);
    }

    [Fact]
    public void FindSuggestions_CapsAtThree()
    {
        var campaigns = Enumerable.Range(1, 5)
            .Select(i => new Campaign
            {
                Id = $"campaigns/camp-{i}/meta",
                Name = $"camp-{i}",
                DisplayName = $"Camp {i}",
                System = RulesetSystem.Dnd5e,
            })
            .ToList();

        var suggestions = CampaignSlugMatcher.FindSuggestions(
            "camp",
            campaigns,
            c => new CampaignSuggestion(c.Name, c.DisplayName, c.System, 0, null));

        Assert.True(suggestions.Count <= 3);
    }
}
