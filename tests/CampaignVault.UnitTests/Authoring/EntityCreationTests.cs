using System;
using System.Collections.Generic;
using CampaignVault.Authoring.Vault;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class EntityCreationTests
{
    [Theory]
    [InlineData("character", true)]
    [InlineData("location", true)]
    [InlineData("quest", true)]
    [InlineData("faction", true)]
    [InlineData("lore", true)]
    [InlineData("rumor", true)]
    [InlineData("event", true)]
    [InlineData("item", true)]
    [InlineData("unknown", false)]
    [InlineData("spell", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedEntityType_ValidatesAgainstEntityFolders(string? type, bool expected)
    {
        var result = EntityCreation.IsSupportedEntityType(type);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("character", "characters")]
    [InlineData("CHARACTER", "characters")]
    [InlineData("location", "locations")]
    [InlineData("quest", "quests")]
    [InlineData("faction", "factions")]
    [InlineData("lore", "lore")]
    [InlineData("rumor", "rumors")]
    [InlineData("event", "events")]
    [InlineData("item", "items")]
    public void GetFolderForType_ReturnsCorrectFolder(string type, string expectedFolder)
    {
        var result = EntityCreation.GetFolderForType(type);
        Assert.Equal(expectedFolder, result);
    }

    [Fact]
    public void GetFolderForType_UnknownType_ThrowsVaultException()
    {
        var ex = Assert.Throws<VaultException>(() => EntityCreation.GetFolderForType("unknown"));
        Assert.Contains("Unsupported entity type", ex.Message);
    }

    [Theory]
    [InlineData("Test NPC", "test-npc")]
    [InlineData("My-Location_V2", "my-location-v2")]
    [InlineData("123", "123")]
    [InlineData("Test  Multiple   Spaces", "test-multiple-spaces")]
    [InlineData("!!!Invalid!!!", "invalid")]
    [InlineData("-leading-and-trailing-", "leading-and-trailing")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("", "")]
    public void ToSlug_GeneratesValidSlug(string name, string expected)
    {
        var result = EntityCreation.ToSlug(name);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildNewEntityPath_UsesVaultPathsFolders_AndCleanSlug()
    {
        var now = new DateTime(2025, 7, 2, 14, 30, 45);
        var (relativePath, slug) = EntityCreation.BuildNewEntityPath("character", "TestChar", now);

        Assert.Equal("characters/testchar.md", relativePath);
        Assert.Equal("testchar", slug);
    }

    [Fact]
    public void BuildNewEntityPath_WithEmptyName_FallsBackToTypeAndTimestamp()
    {
        var now = new DateTime(2025, 7, 2, 14, 30, 45);
        var (relativePath, slug) = EntityCreation.BuildNewEntityPath("quest", "", now);

        Assert.StartsWith("quests/new-quest-20250702143045", relativePath);
        Assert.Equal("new-quest-20250702143045", slug);
    }

    [Fact]
    public void BuildNewEntityPath_MatchesUILogic()
    {
        // Ensure agent and UI code paths produce identical results for same inputs
        var entityType = "faction";
        var name = "The Council";
        var now = DateTime.Parse("2025-07-02T15:45:30");

        var (path, slug) = EntityCreation.BuildNewEntityPath(entityType, name, now);

        var expectedFolder = "factions";
        var expectedSlug = "the-council";

        Assert.Equal($"{expectedFolder}/{expectedSlug}.md", path);
        Assert.Equal(expectedSlug, slug);
    }

    [Fact]
    public void BuildNewEntityPath_WhenSlugExists_DisambiguatesWithSuffix()
    {
        var now = new DateTime(2025, 7, 2, 14, 30, 45);
        var existing = new HashSet<string> { "characters/grog.md", "characters/grog-2.md" };

        var (relativePath, slug) = EntityCreation.BuildNewEntityPath(
            "character", "Grog", now, relativePathExists: existing.Contains);

        Assert.Equal("characters/grog-3.md", relativePath);
        Assert.Equal("grog-3", slug);
    }
}
