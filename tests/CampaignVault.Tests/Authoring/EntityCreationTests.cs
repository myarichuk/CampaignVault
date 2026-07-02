using System;
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
    public void BuildNewEntityPath_UsesVaultPathsFolders()
    {
        var now = new DateTime(2025, 7, 2, 14, 30, 45);
        var (relativePath, slug) = EntityCreation.BuildNewEntityPath("character", "TestChar", now);

        // Should match the pattern: characters/testchar-20250702143045.md
        Assert.StartsWith("characters/", relativePath);
        Assert.EndsWith(".md", relativePath);
        Assert.Contains("testchar-20250702143045", relativePath);
        Assert.Equal("testchar-20250702143045", slug);
    }

    [Fact]
    public void BuildNewEntityPath_WithEmptyName_UsesTypeAndTimestamp()
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
        var expectedSlug = "the-council-20250702154530";

        Assert.Equal($"{expectedFolder}/{expectedSlug}.md", path);
        Assert.Equal(expectedSlug, slug);
    }
}
