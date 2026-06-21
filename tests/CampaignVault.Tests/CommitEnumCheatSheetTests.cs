using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class CommitEnumCheatSheetTests
{
    [Fact]
    public void Compact_ListsCommonEnumMistakes()
    {
        Assert.Contains("Settlement", CommitEnumCheatSheet.Compact);
        Assert.Contains("Conversation", CommitEnumCheatSheet.Compact);
        Assert.Contains("City/Town", CommitEnumCheatSheet.Compact);
        Assert.Contains("Narrative", CommitEnumCheatSheet.Compact);
        Assert.Contains("involved", CommitEnumCheatSheet.Compact);
        Assert.Contains("participants", CommitEnumCheatSheet.Compact);
    }

    [Fact]
    public void Full_IncludesRulesetActionTypes()
    {
        Assert.Contains("SkillCheck", CommitEnumCheatSheet.Full);
        Assert.Contains("Attack", CommitEnumCheatSheet.Full);
        Assert.Contains("Commit Enum Values", CommitEnumCheatSheet.Full);
    }
}
