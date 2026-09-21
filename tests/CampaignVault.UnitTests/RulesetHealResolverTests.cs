using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class RulesetHealResolverTests
{
    private static Dnd5eRulesetResolver CreateResolver(int healResult = 4)
    {
        var roll = Substitute.For<IRollService>();
        roll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new RollOutcome { Result = healResult, Tag = "heal", Summary = healResult.ToString() }));
        return new Dnd5eRulesetResolver(roll);
    }

    private static ChangeContext CreateContext(Character actor)
    {
        return ChangeContextTestHelper.Create(
            characters: new Dictionary<string, Character> { [actor.Id] = actor },
            campaignName: "heal-test");
    }

    [Fact]
    public async Task UseItem_WithHealDice_EmitsHpChange()
    {
        var actor = new Character { Id = "chars/pc", Name = "Hero", SystemStats = new Dnd5eExtension() };
        var output = await CreateResolver(6).ResolveAsync(CreateContext(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.UseItem,
            ActionName = "Potion of Healing",
            Parameters = new Dictionary<string, string> { ["healDice"] = "2d4+2" }
        });

        Assert.True(output.Result.Success, output.Result.Narrative);
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal(6, hp.Delta);
    }

    [Fact]
    public async Task UseItem_WithoutHealParams_DoesNotEmitHpChange()
    {
        var actor = new Character { Id = "chars/pc", Name = "Hero", SystemStats = new Dnd5eExtension() };
        var output = await CreateResolver().ResolveAsync(CreateContext(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.UseItem,
            ActionName = "Torch",
            Parameters = new Dictionary<string, string>()
        });

        Assert.True(output.Result.Success, output.Result.Narrative);
        Assert.Empty(output.Mutations.OfType<HpChange>());
    }

    [Fact]
    public async Task Recovery_WithoutHealParams_FailsInvalidParameter()
    {
        var actor = new Character { Id = "chars/pc", Name = "Hero", SystemStats = new Dnd5eExtension() };
        var output = await CreateResolver().ResolveAsync(CreateContext(actor), new RulesetAction
        {
            CharacterId = actor.Id,
            ActionType = RulesetActionType.Recovery,
            ActionName = "Second Wind",
            Parameters = new Dictionary<string, string>()
        });

        Assert.False(output.Result.Success);
        Assert.Equal("InvalidParameter", output.Result.ErrorCode);
        Assert.Empty(output.Mutations);
    }
}
