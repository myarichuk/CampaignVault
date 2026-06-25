using System.Collections.Generic;
using System.Linq;
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

public class FakeRollService : IRollService
{
    public Queue<RollOutcome> NextRolls { get; } = new();
    public Queue<IReadOnlyList<RollOutcome>> NextBatches { get; } = new();
    public Queue<FalloutCombatDiceResult> NextFalloutRolls { get; } = new();
    public List<RollRequest> RecordedRequests { get; } = [];

    public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default)
    {
        RecordedRequests.Add(request);
        return Task.FromResult(NextRolls.Dequeue());
    }

    public Task<IReadOnlyList<RollOutcome>> RollBatchAsync(IEnumerable<RollRequest> requests,
        CancellationToken ct = default)
    {
        return Task.FromResult(NextBatches.Dequeue());
    }

    public Task<FalloutCombatDiceResult> RollFalloutCombatDiceAsync(int count, CancellationToken ct = default)
    {
        return Task.FromResult(NextFalloutRolls.Dequeue());
    }
}

public class Dnd5eRulesetResolverTests
{
    private ChangeContext CreateContext(params Character[] characters) =>
        CreateContext(items: null, characters);

    private ChangeContext CreateContext(Dictionary<string, Item>? items, params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: items ?? new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0],
                new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null
        );
    }

    [Fact]
    public async Task ResolveAttack_Hit_GeneratesHpChange()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal("char2", hpChange.CharacterId);
        Assert.Equal(-8, hpChange.Delta);
        Assert.Contains("Hit for 8 damage", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_ToHitBonusAlias_AppliesAttackBonus()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 12, HasCritical = false, HasComplication = false, Summary = "[8] + 4 = 12" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Dagger",
            Parameters = new Dictionary<string, string> { ["toHitBonus"] = "4" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(4, rollService.RecordedRequests[0].Bonus);
        Assert.Single(output.Mutations);
        Assert.Contains("Attack 12 vs AC 12", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_Miss_GeneratesNoMutations()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 12, HasCritical = false, HasComplication = false, Summary = "Rolled 12" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 14 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Missed", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_CriticalHit_RollsExtraDamage()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 20, HasCritical = true, HasComplication = false, Summary = "Rolled Nat 20" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });

        var resolver = new Dnd5eRulesetResolver(rollService);

        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension { ArmorClass = 25 } };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Longsword",
            Parameters = new Dictionary<string, string> { ["damageDice"] = "1d8" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Single(output.Mutations);
        var hpChange = Assert.IsType<HpChange>(output.Mutations[0]);
        Assert.Equal(-13, hpChange.Delta);
        Assert.Contains("CRITICAL HIT!", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveSkillCheck_SucceedsAgainstDC()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 16, Summary = "Rolled 16" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character
        {
            Id = "char1",
            SystemStats = new Dnd5eExtension { SkillModifiers = new Dictionary<string, int> { { "Stealth", 5 } } }
        };

        var context = CreateContext(actor);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Sneak",
            Parameters = new Dictionary<string, string> { ["skill"] = "Stealth", ["dc"] = "15" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Empty(output.Mutations);
        Assert.Contains("Success", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_InvalidBonus_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack,
            Parameters = new Dictionary<string, string> { ["bonus"] = "not_a_number" }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("invalid bonus value", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MismatchedTargetExtension_ReturnsError()
    {
        var rollService = new FakeRollService();
        var resolver = new Dnd5eRulesetResolver(rollService);

        // Actor is correct, but target is using a different system's extension (e.g. Pf2eExtension or base SystemExtension)
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Pf2eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.Attack
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("incompatible ruleset stats", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveContestedCheck_Success()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 18 }); // Actor
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 12 }); // Target

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };

        var context = CreateContext(actor, target);
        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.ContestedCheck,
            ActionName = "Grapple"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Contains("Actor Wins", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAsync_SavingThrow_UsesAdvantageState()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 15, Summary = "Rolled 15" }));

        var resolver = new Dnd5eRulesetResolver(mockRollService);

        var CharacterId = "char_1";
        var actor = new Character { Id = "test-char", SystemStats = new Dnd5eExtension { Dexterity = 14 } };
        var context = CreateContext(actor);

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            ActionType = RulesetActionType.SavingThrow,
            ActionName = "Dexterity Save",
            AdvantageState = AdvantageState.Advantage,
            Parameters = new Dictionary<string, string> { { "dc", "14" }, { "save", "Dexterity" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        await mockRollService.Received(1).RollAsync(Arg.Is<RollRequest>(req => req.Mechanic == DiceMechanic.Advantage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAttackAsync_AppliesDamageResistance()
    {
        var mockRollService = Substitute.For<IRollService>();
        mockRollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new RollOutcome { Result = 20, Summary = "Hit" }), // Attack
                Task.FromResult(new RollOutcome { Result = 10, Summary = "Damage" }) // Damage
            );

        var resolver = new Dnd5eRulesetResolver(mockRollService);

        var CharacterId = "char_1";
        var targetId = "char_2";
        var context = CreateContext(
            new Character { Id = "test-char", SystemStats = new Dnd5eExtension() },
            new Character
            {
                Id = targetId,
                SystemStats = new Dnd5eExtension
                    { DamageModifiers = new Dictionary<string, float> { { "Fire", 0.5f } } }
            }
        );

        var action = new RulesetAction
        {
            CharacterId = "test-char",
            TargetIds = [targetId],
            ActionType = RulesetActionType.Attack,
            ActionName = "Fire Bolt",
            DamageType = "Fire",
            Parameters = new Dictionary<string, string> { { "damageDice", "1d10" } }
        };

        var output = await resolver.ResolveAsync(context, action);

        var hpChange = output.Mutations.OfType<HpChange>().FirstOrDefault();
        Assert.NotNull(hpChange);
        Assert.Equal(-5, hpChange.Delta); // 10 damage * 0.5 resistance
    }

    [Fact]
    public async Task ResolveAttack_AutoAppliesHeldWeaponProperties_ByActionName()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 18, HasCritical = false, HasComplication = false, Summary = "Rolled 18" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 9, Summary = "Rolled 9" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "chars/valen", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "chars/merc-1", Name = "Merc", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var schlag = new Item
        {
            Id = "items/schlag",
            Name = "Schlag",
            HolderId = "chars/valen",
            CoreCategory = ItemCategory.Weapon,
            Properties = new Dictionary<string, object>
            {
                ["damageDice"] = "1d10",
                ["bonus"] = "9",
                ["damageBonus"] = "5"
            }
        };

        var context = CreateContext(
            new Dictionary<string, Item> { [schlag.Id] = schlag },
            actor,
            target);

        var action = new RulesetAction
        {
            CharacterId = "chars/valen",
            TargetIds = ["chars/merc-1"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Schlag"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(9, rollService.RecordedRequests[0].Bonus);
        Assert.Equal("1d10", rollService.RecordedRequests[1].Expression);
        Assert.Equal(5, rollService.RecordedRequests[1].Bonus);
        Assert.Single(output.Mutations);
        Assert.Contains("Schlag vs Merc: Hit for", output.Result.Narrative);
    }

    [Fact]
    public async Task ResolveAttack_MultiTarget_ResolvesSeparateRollsPerTarget()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 15, HasCritical = false, HasComplication = false, Summary = "Rolled 15" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });
        rollService.NextRolls.Enqueue(new RollOutcome
            { Result = 16, HasCritical = false, HasComplication = false, Summary = "Rolled 16" });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "chars/valen", SystemStats = new Dnd5eExtension() };
        var merc1 = new Character { Id = "chars/merc-1", Name = "Merc 1", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };
        var merc2 = new Character { Id = "chars/merc-2", Name = "Merc 2", SystemStats = new Dnd5eExtension { ArmorClass = 12 } };

        var context = CreateContext(actor, merc1, merc2);
        var action = new RulesetAction
        {
            CharacterId = "chars/valen",
            TargetIds = ["chars/merc-1", "chars/merc-2"],
            ActionType = RulesetActionType.Attack,
            ActionName = "Schlag",
            Parameters = new Dictionary<string, string>
            {
                ["damageDice"] = "1d6",
                ["attackCount"] = "2"
            }
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.Equal(2, output.Mutations.Count);
        Assert.Contains("Merc 1", output.Result.Narrative);
        Assert.Contains("Merc 2", output.Result.Narrative);
    }
}
