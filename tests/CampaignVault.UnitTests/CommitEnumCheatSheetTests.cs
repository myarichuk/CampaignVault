using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class CommitEnumCheatSheetTests
{
    [Fact]
    public void Full_IncludesRulesetActionTypes()
    {
        Assert.Contains("SkillCheck", CommitEnumCheatSheet.Full);
        Assert.Contains("Attack", CommitEnumCheatSheet.Full);
        Assert.Contains("Commit Enum Values", CommitEnumCheatSheet.Full);
        Assert.Contains("scene_interrupt_check", CommitEnumCheatSheet.Full);
        Assert.Contains("SceneInterrupt", CommitEnumCheatSheet.Full);
    }

    [Fact]
    public void Full_ListsCommonEnumMistakesAndGuidance()
    {
        Assert.Contains("Settlement", CommitEnumCheatSheet.Full);
        Assert.Contains("Conversation", CommitEnumCheatSheet.Full);
        Assert.Contains("Narrative", CommitEnumCheatSheet.Full);
        Assert.Contains("involved", CommitEnumCheatSheet.Full);
        Assert.Contains("world_build", CommitEnumCheatSheet.Full);
        Assert.Contains("rumors", CommitEnumCheatSheet.Full);
        Assert.DoesNotContain(", Meta", CommitEnumCheatSheet.Full);
    }
}
