using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class RelationshipModifierTests
{
    [Fact]
    public void GetSocialModifier_TrustedFriend_ReturnsPositiveModifier()
    {
        var target = new Character { Id = "target" };
        var actor = new Character { Id = "actor" };
        var config = new CampaignConfig();

        target.Social.Relationships["actor"] = 85;

        var (modifier, label) = RelationshipModifierHelper.GetSocialModifier(target, actor, config);

        Assert.Equal(5, modifier);
        Assert.Equal("trusted friend", label);
    }

    [Fact]
    public void GetSocialModifier_HatedEnemy_ReturnsNegativeModifier()
    {
        var target = new Character { Id = "target" };
        var actor = new Character { Id = "actor" };
        var config = new CampaignConfig();

        target.Social.Relationships["actor"] = -85;

        var (modifier, label) = RelationshipModifierHelper.GetSocialModifier(target, actor, config);

        Assert.Equal(-5, modifier);
        Assert.Equal("hated enemy", label);
    }

    [Fact]
    public void GetSocialModifier_Neutral_ReturnsZeroModifier()
    {
        var target = new Character { Id = "target" };
        var actor = new Character { Id = "actor" };
        var config = new CampaignConfig();

        var (modifier, label) = RelationshipModifierHelper.GetSocialModifier(target, actor, config);

        Assert.Equal(0, modifier);
        Assert.Equal("neutral", label);
    }

    [Fact]
    public void GetSocialModifier_SymmetricFallback_UsesHalfOfReverseRelationship()
    {
        var target = new Character { Id = "target" };
        var actor = new Character { Id = "actor" };
        var config = new CampaignConfig { SymmetricRelationshipFallback = true };

        actor.Social.Relationships["target"] = 120;

        var (modifier, label) = RelationshipModifierHelper.GetSocialModifier(target, actor, config);

        Assert.Equal(3, modifier);
        Assert.Equal("friendly", label);
    }

    [Fact]
    public void GetSocialModifier_SymmetricFallback_ExplicitZeroStaysNeutral()
    {
        var target = new Character { Id = "target" };
        var actor = new Character { Id = "actor" };
        var config = new CampaignConfig { SymmetricRelationshipFallback = true };

        target.Social.Relationships["actor"] = 0;
        actor.Social.Relationships["target"] = 120;

        var (modifier, label) = RelationshipModifierHelper.GetSocialModifier(target, actor, config);

        Assert.Equal(0, modifier);
        Assert.Equal("neutral", label);
    }

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, "Persuasion", true)]
    [InlineData(RulesetSystem.Dnd5e, "Athletics", false)]
    [InlineData(RulesetSystem.Pathfinder2e, "Diplomacy", true)]
    [InlineData(RulesetSystem.Pathfinder2e, "Persuasion", false)]
    public void SocialSkillGating_UsesPerRulesetSkillLists(string system, string skill, bool expected)
    {
        var action = new RulesetAction
        {
            ActionType = RulesetActionType.SkillCheck,
            Parameters = new Dictionary<string, string> { { "skill", skill } }
        };

        Assert.Equal(expected, SocialSkillGating.ShouldApplyRelationshipModifier(system, action, skill));
    }

    [Fact]
    public async Task Dnd5eSkillCheck_NullConfig_StillAppliesRelationshipModifier()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 14, HasCritical = false, HasComplication = false, Summary = "Rolled 14" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "actor", SystemStats = new Dnd5eExtension { Charisma = 16 } };
        var target = new Character { Id = "target" };
        target.Social.Relationships["actor"] = 85;

        var charDict = new Dictionary<string, Character> { { "actor", actor }, { "target", target } };

        var context = ChangeContextTestHelper.Create(
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0],
                new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance)
        );

        var action = new RulesetAction
        {
            CharacterId = "actor",
            ActionName = "Persuade",
            ActionType = RulesetActionType.SkillCheck,
            ActionCategory = ActionCategory.Social,
            TargetIds = ["target"],
            Parameters = new Dictionary<string, string> { { "skill", "Persuasion" }, { "dc", "15" } }
        };

        var result = await resolver.ResolveAsync(context, action);

        Assert.True(result.Result.Success);
        Assert.Contains("trusted friend", result.Result.Narrative);
    }

    [Fact]
    public async Task Dnd5eSkillCheck_Social_IncludesRelationshipModifier()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 14, HasCritical = false, HasComplication = false, Summary = "Rolled 14" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "actor", SystemStats = new Dnd5eExtension { Charisma = 16 } };
        var target = new Character { Id = "target" };
        target.Social.Relationships["actor"] = 85;

        var charDict = new Dictionary<string, Character> { { "actor", actor }, { "target", target } };
        var config = new CampaignConfig();

        var context = ChangeContextTestHelper.Create(
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0],
                new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            config: config
        );

        var action = new RulesetAction
        {
            CharacterId = "actor",
            ActionName = "Persuade",
            ActionType = RulesetActionType.SkillCheck,
            ActionCategory = ActionCategory.Social,
            TargetIds = ["target"],
            Parameters = new Dictionary<string, string> { { "skill", "Persuasion" }, { "dc", "15" } }
        };

        var result = await resolver.ResolveAsync(context, action);

        Assert.True(result.Result.Success);
        Assert.Contains("trusted friend", result.Result.Narrative);
    }

    [Fact]
    public async Task Dnd5eSkillCheck_NonSocial_IgnoresRelationshipModifier()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 14, HasCritical = false, HasComplication = false, Summary = "Rolled 14" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "actor", SystemStats = new Dnd5eExtension { Strength = 16 } };
        var target = new Character { Id = "target" };
        target.Social.Relationships["actor"] = 85;

        var charDict = new Dictionary<string, Character> { { "actor", actor }, { "target", target } };
        var config = new CampaignConfig();

        var context = ChangeContextTestHelper.Create(
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0],
                new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            config: config
        );

        var action = new RulesetAction
        {
            CharacterId = "actor",
            ActionName = "Climb",
            ActionType = RulesetActionType.SkillCheck,
            TargetIds = ["target"],
            Parameters = new Dictionary<string, string> { { "skill", "Athletics" }, { "dc", "15" } }
        };

        var result = await resolver.ResolveAsync(context, action);

        Assert.True(result.Result.Success);
        Assert.DoesNotContain("trusted friend", result.Result.Narrative);
    }

    [Fact]
    public async Task Dnd5eContestedCheck_Social_AppliesRelationshipModifierToActor()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 18, HasCritical = false, HasComplication = false, Summary = "Rolled 18" });
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 10, HasCritical = false, HasComplication = false, Summary = "Rolled 10" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "actor", SystemStats = new Dnd5eExtension { Charisma = 16 } };
        var target = new Character
        {
            Id = "target",
            SystemStats = new Dnd5eExtension { Strength = 14 }
        };
        target.Social.Relationships["actor"] = 60;

        var charDict = new Dictionary<string, Character> { { "actor", actor }, { "target", target } };
        var config = new CampaignConfig();

        var context = ChangeContextTestHelper.Create(
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0],
                new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            config: config
        );

        var action = new RulesetAction
        {
            CharacterId = "actor",
            ActionName = "Persuade",
            ActionType = RulesetActionType.ContestedCheck,
            ActionCategory = ActionCategory.Social,
            TargetIds = ["target"],
            Parameters = new Dictionary<string, string> { { "skill", "Persuasion" } }
        };

        var result = await resolver.ResolveAsync(context, action);

        Assert.True(result.Result.Success);
        Assert.Contains("friendly", result.Result.Narrative);
        Assert.Contains("Actor Wins", result.Result.Narrative);
    }
}
