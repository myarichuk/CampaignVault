using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

public class SideEffectDuplicationGuardTests
{
    [Fact]
    public void FindConflict_RulesetActionWithSceneSetupEngagement_SameCharacter_ReturnsConflict()
    {
        var changes = new WorldChange[]
        {
            new RulesetAction { CharacterId = "chars/1", ActionName = "Grapple", ActionType = RulesetActionType.ContestedCheck, TargetIds = ["chars/2"] },
            new SceneSetupChange
            {
                CharacterId = "chars/1",
                TargetId = "chars/2",
                Engagement = new SceneSetupEngagement { Verb = "Grappling" }
            }
        };

        var conflict = SideEffectDuplicationGuard.FindConflict(changes);

        Assert.NotNull(conflict);
        Assert.Contains("engagement_relation", conflict);
    }

    [Fact]
    public void FindConflict_RulesetActionWithSceneSetupSpatialOnly_NoConflict()
    {
        var changes = new WorldChange[]
        {
            new RulesetAction { CharacterId = "chars/1", ActionName = "Grapple", ActionType = RulesetActionType.ContestedCheck, TargetIds = ["chars/2"] },
            new SceneSetupChange
            {
                CharacterId = "chars/1",
                TargetId = "chars/2",
                Spatial = new SceneSetupSpatial { DistanceBand = "Touch" }
            }
        };

        var conflict = SideEffectDuplicationGuard.FindConflict(changes);

        Assert.Null(conflict);
    }
}
