using Xunit;
using CampaignVault.Authoring.Services;
using CampaignVault.Models;

namespace CampaignVault.Tests.Authoring;

public class WorkspaceParserTests
{
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

        var parser = new WorkspaceParser();
        var character = parser.ParseCharacter(markdown);

        Assert.NotNull(character);
        Assert.Equal("test_char", character.Id);
        Assert.Equal("Test Character", character.Name);
        Assert.Equal(10, character.CurrentHp);
        Assert.Equal(20, character.MaxHp);
        Assert.Contains("This is a note.", character.Notes);
    }
}
