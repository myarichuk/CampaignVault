using System;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class WorkspaceParserTests
{
    private readonly WorkspaceParser _parser = new();

    [Fact]
    public void ParseCharacter_ValidMarkdown_ExtractsYamlAndBody()
    {
        var markdown = @"---
$type: ""character""
id: ""test_char""
name: ""Test Character""
currentHp: 10
maxHp: 20
---
# Test Body
This is a note.";

        var character = _parser.ParseCharacter(markdown);

        Assert.NotNull(character);
        Assert.Equal("test_char", character.Id);
        Assert.Equal("Test Character", character.Name);
        Assert.Equal(10, character.CurrentHp);
        Assert.Equal(20, character.MaxHp);
        Assert.Contains("This is a note.", character.Notes);
    }

    [Fact]
    public void ParseCharacter_MissingFrontmatter_ThrowsArgumentException()
    {
        var markdown = "# Just a body\nNo frontmatter here.";
        Assert.Throws<ArgumentException>(() => _parser.ParseCharacter(markdown));
    }

    [Fact]
    public void ParseCharacter_EmptyBody_ParsesCorrectly()
    {
        var markdown = @"---
$type: ""character""
id: ""test_char""
---";
        var character = _parser.ParseCharacter(markdown);
        Assert.NotNull(character);
        Assert.Equal("test_char", character.Id);
        Assert.Equal(string.Empty, character.Notes);
    }

    [Fact]
    public void ParseFaction_BodyMapsToDescription()
    {
        var markdown = """
            ---
            id: factions/guild
            name: Thieves Guild
            factionType: criminal
            ---
            Underground network.
            """;

        var faction = _parser.ParseFaction(markdown);
        Assert.Equal("Underground network.", faction.Description);
    }

    [Fact]
    public void ParseLore_BodyMapsToContent()
    {
        var markdown = """
            ---
            id: lore/tale
            title: Tale
            ---
            Once upon a time.
            """;

        var lore = _parser.ParseLore(markdown);
        Assert.Equal("Once upon a time.", lore.Content);
    }

    [Fact]
    public void ParseRumor_BodyMapsToCurrentText()
    {
        var markdown = """
            ---
            id: rumors/gossip
            regionLocationId: locations/tavern
            subject: Gossip
            state: spreading
            truthValue: partiallyTrue
            dayCreated: 1
            lastStateChangeDay: 1
            ---
            Heard at the bar.
            """;

        var rumor = _parser.ParseRumor(markdown);
        Assert.Equal("Heard at the bar.", rumor.CurrentText);
    }

    [Fact]
    public void ParseEvent_BodyMapsToSummary()
    {
        var markdown = """
            ---
            id: events/ambush
            category: combat
            dayLogged: 2
            ---
            Ambush on the road.
            """;

        var evt = _parser.ParseEvent(markdown);
        Assert.Equal("Ambush on the road.", evt.Summary);
    }

    [Fact]
    public void ParseCharacter_CRLFLineEndings_ParsesCorrectly()
    {
        var markdown = "---\r\n$type: \"character\"\r\nid: \"test_char\"\r\n---\r\nBody";
        var character = _parser.ParseCharacter(markdown);
        Assert.NotNull(character);
        Assert.Equal("test_char", character.Id);
        Assert.Equal("Body", character.Notes);
    }
}
