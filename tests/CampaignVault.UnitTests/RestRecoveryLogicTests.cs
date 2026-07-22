using System.Collections.Generic;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Pure-logic tests for RestRecoveryLogic.BuildTirednessRecoveryDelta — the tiredness-settles-toward-
/// baseline recovery added to rest so it doesn't come out backwards (a rest raising tiredness via the
/// day-tick, with nothing to counteract it).
/// </summary>
public class RestRecoveryLogicTests
{
    [Fact]
    public void BuildTirednessRecoveryDelta_LongRest_SettlesFullyToBaseline()
    {
        var character = new Character
        {
            Id = "chars/1",
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 90f } }
        };

        var delta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, RestType.LongRest, baseline: 20);

        var need = Assert.IsType<NeedChange>(delta);
        Assert.Equal("chars/1", need.CharacterId);
        Assert.Equal("tiredness", need.Need);
        Assert.Equal(-70f, need.Delta, precision: 3);
    }

    [Fact]
    public void BuildTirednessRecoveryDelta_ShortRest_SettlesHalfway()
    {
        var character = new Character
        {
            Id = "chars/1",
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 90f } }
        };

        var delta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, RestType.ShortRest, baseline: 20);

        var need = Assert.IsType<NeedChange>(delta);
        Assert.Equal(-35f, need.Delta, precision: 3);
    }

    [Fact]
    public void BuildTirednessRecoveryDelta_PerTurnRest_ReturnsNull()
    {
        var character = new Character
        {
            Id = "chars/1",
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 90f } }
        };

        var delta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, RestType.PerTurn, baseline: 20);

        Assert.Null(delta);
    }

    [Fact]
    public void BuildTirednessRecoveryDelta_NoNeedsProfile_ReturnsNull()
    {
        var character = new Character { Id = "chars/1", Needs = null };

        var delta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, RestType.LongRest, baseline: 20);

        Assert.Null(delta);
    }

    [Fact]
    public void BuildTirednessRecoveryDelta_AlreadyAtOrBelowBaseline_ReturnsNull()
    {
        var character = new Character
        {
            Id = "chars/1",
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 10f } }
        };

        var delta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, RestType.LongRest, baseline: 20);

        Assert.Null(delta);
    }
}
