using System;
using System.Text.Json;
using CampaignVault.Authoring.Vault.Canonical;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests.Authoring.Vault.Canonical;

public sealed class EntityCanonicalizerTests
{
    private readonly EntityCanonicalizer _canonicalizer = new();

    [Theory]
    [InlineData("character")]
    [InlineData("location")]
    [InlineData("quest")]
    [InlineData("faction")]
    [InlineData("lore")]
    [InlineData("rumor")]
    [InlineData("event")]
    [InlineData("customcreature")]
    [InlineData("plotthread")]
    public void RoundTrip_AllEntityTypes_ProducesStableCanonicalHash(string entityType)
    {
        var markdown = MinimalMarkdown(entityType);
        var json = _canonicalizer.MarkdownToJson(entityType, markdown);
        var hash1 = _canonicalizer.ComputeCanonicalHash(entityType, markdown);
        var canonicalOnce = _canonicalizer.JsonToMarkdown(entityType, json);
        var json2 = _canonicalizer.MarkdownToJson(entityType, canonicalOnce);
        var hash2 = _canonicalizer.ComputeCanonicalHashFromJson(entityType, json2);

        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains("---", canonicalOnce);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void JsonToMarkdown_CharacterFromRemoteJson_MatchesExpectedShape()
    {
        var remoteJson = """
            {
              "id": "characters/grog",
              "name": "Grog",
              "currentHp": 10,
              "maxHp": 20,
              "notes": "Brave warrior.",
              "campaignName": "test-campaign",
              "lastUpdated": "2026-06-25T12:00:00Z"
            }
            """;

        var markdown = _canonicalizer.JsonToMarkdown("character", remoteJson);

        Assert.Contains("id: characters/grog", markdown);
        Assert.Contains("name: Grog", markdown);
        Assert.DoesNotContain("campaignName", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Brave warrior.", markdown);
        Assert.EndsWith("\n", markdown);
    }

    [Fact]
    public void ComputeCanonicalHashFromJson_ServerSerializedCharacter_MatchesMarkdownHash()
    {
        var character = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CurrentHp = 10,
            MaxHp = 20,
            Notes = "Brave warrior.",
            CampaignName = "test-campaign"
        };

        var serverJson = JsonSerializer.Serialize(character);
        var markdown = _canonicalizer.JsonToMarkdown("character", serverJson);

        var hashFromServer = _canonicalizer.ComputeCanonicalHashFromJson("character", serverJson);
        var hashFromMarkdown = _canonicalizer.ComputeCanonicalHash("character", markdown);

        Assert.Equal(hashFromServer, hashFromMarkdown);
    }

    [Fact]
    public void MarkdownToJson_ExcludesBodyFieldsFromDuplicateStorage()
    {
        var markdown = """
            ---
            id: lore/ancient-tale
            title: Ancient Tale
            ---
            Once upon a time.
            """;

        var json = _canonicalizer.MarkdownToJson("lore", markdown);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Once upon a time.", doc.RootElement.GetProperty("content").GetString());
    }

    private static string MinimalMarkdown(string entityType) =>
        entityType switch
        {
            "character" => """
                ---
                id: characters/grog
                name: Grog
                currentHp: 10
                maxHp: 20
                ---
                Notes about Grog.
                """,
            "location" => """
                ---
                id: locations/tavern
                name: Rusty Nail
                type: building
                ---
                A cozy tavern.
                """,
            "quest" => """
                ---
                id: quests/rescue
                title: Rescue Mission
                ---
                DM-only notes.
                """,
            "faction" => """
                ---
                id: factions/thieves-guild
                name: Thieves Guild
                factionType: criminal
                ---
                Underground network.
                """,
            "lore" => """
                ---
                id: lore/prophecy
                title: The Prophecy
                ---
                Ancient words.
                """,
            "rumor" => """
                ---
                id: rumors/missing-merchant
                regionLocationId: locations/tavern
                subject: Missing Merchant
                state: spreading
                truthValue: partiallyTrue
                dayCreated: 1
                lastStateChangeDay: 1
                ---
                Last seen near the bridge.
                """,
            "event" => """
                ---
                id: events/ambush
                category: combat
                dayLogged: 3
                ---
                Party ambushed on the road.
                """,
            "customcreature" => """
                ---
                id: creatures/giant-rat-swarm
                name: Giant Rat Swarm
                system: dnd5e
                challengeRating: "1/4"
                hp: 7
                ---
                A swarm of oversized vermin.
                """,
            "plotthread" => """
                ---
                id: plotthreads/the-hidden-cult
                title: The Hidden Cult
                state: active
                tensionLevel: 10
                ---
                DM notes on the cult's plans.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(entityType))
        };
}