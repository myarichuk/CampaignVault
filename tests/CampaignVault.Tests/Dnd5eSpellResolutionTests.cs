using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class Dnd5eSpellResolutionTests
{
    private static ChangeContext CreateContext(params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                [],
                new CampaignVault.Data.CampaignDocumentKeys(),
                NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);
    }

    [Fact]
    public async Task ResolveSpell_Save_AppliesDamageToTargets()
    {
        var mockRoll = Substitute.For<IRollService>();
        mockRoll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(call =>
            {
                var req = call.Arg<RollRequest>();
                return Task.FromResult(new RollOutcome
                {
                    Result = req.Tag == "spell-damage" ? 20 : 8,
                    Summary = "rolled",
                });
            });

        var resolver = new Dnd5eRulesetResolver(mockRoll);
        var caster = new Character
        {
            Id = "caster",
            SystemStats = new Dnd5eExtension
            {
                Level = 5,
                Intelligence = 16,
                SpellcastingAbility = "Intelligence",
                SpellSaveDc = 15,
            },
        };
        var target = new Character
        {
            Id = "target",
            Name = "Goblin",
            SystemStats = new Dnd5eExtension { Dexterity = 10 },
        };

        var action = new RulesetAction
        {
            CharacterId = "caster",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Spell,
            ActionCategory = ActionCategory.Spell,
            ActionName = "Fireball",
            Parameters = new Dictionary<string, string>
            {
                ["resolution"] = "save",
                ["save"] = "Dexterity",
                ["damageDice"] = "8d6",
            },
        };

        var output = await resolver.ResolveAsync(CreateContext(caster, target), action);

        Assert.True(output.Result.Success);
        var hp = Assert.Single(output.Mutations.OfType<HpChange>());
        Assert.Equal("target", hp.CharacterId);
        Assert.Equal(-20, hp.Delta);
        Assert.Contains("Failed", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpell_Utility_Check_UsesArcanaWithoutTargets()
    {
        var mockRoll = Substitute.For<IRollService>();
        mockRoll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 18, Summary = "18" }));

        var resolver = new Dnd5eRulesetResolver(mockRoll);
        var caster = new Character
        {
            Id = "caster",
            SystemStats = new Dnd5eExtension
            {
                Intelligence = 16,
                SpellcastingAbility = "Intelligence",
                SkillModifiers = new Dictionary<string, int> { ["Arcana"] = 5 },
            },
        };

        var action = new RulesetAction
        {
            CharacterId = "caster",
            ActionType = RulesetActionType.Spell,
            ActionCategory = ActionCategory.Spell,
            ActionName = "Detect Magic",
            Parameters = new Dictionary<string, string>
            {
                ["resolution"] = "check",
                ["dc"] = "15",
                ["skill"] = "Arcana",
            },
        };

        var output = await resolver.ResolveAsync(CreateContext(caster), action);

        Assert.True(output.Result.Success);
        Assert.Empty(output.Mutations);
        Assert.Contains("Success", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSpell_Attack_UsesDerivedSpellAttackBonus()
    {
        var mockRoll = Substitute.For<IRollService>();
        mockRoll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(call =>
            {
                var req = call.Arg<RollRequest>();
                return Task.FromResult(new RollOutcome
                {
                    Result = req.Tag == "damage" ? 6 : 17,
                    HasCritical = false,
                    HasComplication = false,
                    Summary = "rolled",
                });
            });

        var resolver = new Dnd5eRulesetResolver(mockRoll);
        var caster = new Character
        {
            Id = "caster",
            SystemStats = new Dnd5eExtension
            {
                Level = 5,
                Intelligence = 16,
                SpellcastingAbility = "Intelligence",
                SpellAttackBonus = 8,
            },
        };
        var target = new Character
        {
            Id = "target",
            Name = "Bandit",
            SystemStats = new Dnd5eExtension { ArmorClass = 12 },
        };

        var action = new RulesetAction
        {
            CharacterId = "caster",
            TargetIds = ["target"],
            ActionType = RulesetActionType.Spell,
            ActionName = "Fire Bolt",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d10" },
        };

        var output = await resolver.ResolveAsync(CreateContext(caster, target), action);

        await mockRoll.Received().RollAsync(
            Arg.Is<RollRequest>(r => r.Tag == "attack" && r.Bonus == 8),
            Arg.Any<System.Threading.CancellationToken>());
        Assert.Contains("Hit for 6 damage", output.Result.Narrative);
    }
}
