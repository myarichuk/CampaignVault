using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class NarrativeRulesetResolverTests
{
    private readonly IRollService _rollServiceSub;
    private readonly NarrativeRulesetResolver _resolver;
    private readonly Character _actor;
    private readonly Character _target;

    public NarrativeRulesetResolverTests()
    {
        _rollServiceSub = Substitute.For<IRollService>();
        _resolver = new NarrativeRulesetResolver(_rollServiceSub);
        _actor = new Character { Id = "chars/hero", Name = "Hero" };
        _target = new Character { Id = "chars/villain", Name = "Villain" };
    }

    [Fact]
    public async Task RollInitiative_ReturnsRandomValue()
    {
        // Narrative combat just needs a sort order, no complex math.
        _rollServiceSub.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 5, Summary = "Narrative Initiative" }));

        var init = await _resolver.Combat.RollInitiativeAsync(_actor);

        Assert.Equal(5f, init);
    }

    [Theory]
    [InlineData(1, false, "No, And")]
    [InlineData(2, false, "No.")]
    [InlineData(3, false, "No, But")]
    [InlineData(4, true, "Yes, But")]
    [InlineData(5, true, "Yes.")]
    [InlineData(6, true, "Yes, And")]
    public async Task ResolveAsync_SkillCheck_UsesOracle(int rollResult, bool expectedSuccess, string expectedNarrativePrefix)
    {
        _rollServiceSub.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = rollResult, Summary = $"Oracle: {rollResult}" }));

        var action = new RulesetAction
        {
            ActionType = RulesetActionType.SkillCheck,
            ActionCategory = ActionCategory.Social,
            ActionName = "Persuade",
            CharacterId = _actor.Id
        };

        var chars = new Dictionary<string, Character> { { _actor.Id, _actor } };
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = new ChangeContext(
            null, chars, new Dictionary<string, Item>(), new Dictionary<string, Location>(),
            null, null, NullLogger.Instance, new List<string>(), dispatcher
        );

        var output = await _resolver.Actions.ResolveAsync(context, action);

        Assert.Equal(expectedSuccess, output.Result.Success);
        Assert.Contains(expectedNarrativePrefix, output.Result.Narrative);
        
        // Complications add a NeedChange
        if (rollResult == 1 || rollResult == 4)
        {
            Assert.Contains(output.Mutations, m => m is NeedChange nc && nc.Need == "stress");
        }
    }

    [Fact]
    public async Task ResolveAsync_Attack_AppliesConditionInsteadOfComplexDamage()
    {
        // Roll 5: Yes
        _rollServiceSub.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 5, Summary = "Oracle: 5" }));

        var action = new RulesetAction
        {
            ActionType = RulesetActionType.Attack,
            ActionCategory = ActionCategory.Melee,
            ActionName = "Strike",
            CharacterId = _actor.Id,
            TargetIds = [ _target.Id ]
        };

        var chars = new Dictionary<string, Character> { { _actor.Id, _actor }, { _target.Id, _target } };
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        var context = new ChangeContext(
            null, chars, new Dictionary<string, Item>(), new Dictionary<string, Location>(),
            null, null, NullLogger.Instance, new List<string>(), dispatcher
        );

        var output = await _resolver.Actions.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        
        // Narrative attacks apply conditions or flat 1 HP hits
        Assert.Contains(output.Mutations, m => m is HpChange hp && hp.CharacterId == _target.Id && hp.Delta == -1);
    }
}
