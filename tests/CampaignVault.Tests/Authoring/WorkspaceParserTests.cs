using System;
using Xunit;
using CampaignVault.Authoring.Services;
using CampaignVault.Models;

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
    public void ParseCharacter_CRLFLineEndings_ParsesCorrectly()
    {
        var markdown = "---\r\n$type: \"character\"\r\nid: \"test_char\"\r\n---\r\nBody";
        var character = _parser.ParseCharacter(markdown);
        Assert.NotNull(character);
        Assert.Equal("test_char", character.Id);
        Assert.Equal("Body", character.Notes);
    }
}
