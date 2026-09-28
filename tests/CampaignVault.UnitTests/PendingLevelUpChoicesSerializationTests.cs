using System.Text.Json;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class PendingLevelUpChoicesSerializationTests
{
    [Fact]
    public void Response_WithStringSystem_Serializes()
    {
        var response = new PendingLevelUpChoicesResponse
        {
            CharacterId = "chars/hero",
            ClassName = "fighter",
            CurrentLevel = 1,
            TargetLevel = 2,
            System = RulesetSystem.Dnd5e,
            Summary = "ok",
        };

        var json = JsonSerializer.Serialize<object>(response);

        Assert.Contains(RulesetSystem.Dnd5e, json);
    }
}
