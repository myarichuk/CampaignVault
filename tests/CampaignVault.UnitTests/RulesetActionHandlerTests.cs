using System;
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

[Collection("RavenDB")]
public class RulesetActionHandlerTests : IClassFixture<RavenDBFixture>
{
    // Unique per test-class instance (xUnit constructs a fresh instance per test method) so
    // document IDs don't collide with other test files sharing the same embedded RavenDB database.
    private readonly string _campaign = "test-campaign-" + Guid.NewGuid().ToString("N")[..8];

    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public RulesetActionHandlerTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private IRulesetModuleSelector CreateSelector(IRollService rollService)
    {
        IRulesetModule[] modules =
        [
            new Dnd5eRulesetResolver(rollService),
            new Pf2eRulesetResolver(rollService),
            new NarrativeRulesetResolver(rollService),
        ];
        return new RulesetModuleSelector(modules);
    }

    private async Task StoreConfigAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string activeSystem)
    {
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = activeSystem });
        await session.SaveChangesAsync();
    }

    private ChangeContext CreateContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        Dictionary<string, Character> characters,
        Dictionary<string, Item>? items = null,
        CombatEncounter? activeCombat = null)
    {
        var dispatcher = new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);
        return new ChangeContext(
            session,
            characters,
            items ?? new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            dispatcher,
            activeCombat,
            _campaign);
    }

    [Fact]
    public async Task ApplyAsync_AttackWithWeaponRange_BlocksOutOfRangeTarget()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);

        var actor = new Character
        {
            Id = "chars/actor",
            SystemStats = new Dnd5eExtension
            {
                SpatialPositions = [new SpatialPosition { TargetId = "chars/target", DistanceBand = SpatialDistanceBand.Far }]
            }
        };
        var target = new Character { Id = "chars/target", SystemStats = new Dnd5eExtension() };
        var weapon = new Item
        {
            Id = "items/bow",
            Name = "Shortbow",
            HolderId = actor.Id,
            CoreCategory = ItemCategory.Weapon,
            Properties = new Dictionary<string, object> { ["range"] = "Near" }
        };

        var characters = new Dictionary<string, Character> { [actor.Id] = actor, [target.Id] = target };
        var items = new Dictionary<string, Item> { [weapon.Id] = weapon };
        var context = CreateContext(session, characters, items);

        var selector = CreateSelector(Substitute.For<IRollService>());
        var handler = new RulesetActionHandler(selector, _keys);

        var action = new RulesetAction
        {
            CharacterId = actor.Id,
            TargetIds = [target.Id],
            ActionType = RulesetActionType.Attack,
            ActionName = "Shortbow",
        };

        var result = await handler.ApplyAsync(action, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("OutOfRange", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_AttackWithWeaponRange_AllowsInRangeTarget()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);

        var actor = new Character
        {
            Id = "chars/actor",
            SystemStats = new Dnd5eExtension
            {
                SpatialPositions = [new SpatialPosition { TargetId = "chars/target", DistanceBand = SpatialDistanceBand.Close }]
            }
        };
        var target = new Character { Id = "chars/target", SystemStats = new Dnd5eExtension { ArmorClass = 10 } };
        var weapon = new Item
        {
            Id = "items/bow",
            Name = "Shortbow",
            HolderId = actor.Id,
            CoreCategory = ItemCategory.Weapon,
            Properties = new Dictionary<string, object> { ["range"] = "Near" }
        };

        var characters = new Dictionary<string, Character> { [actor.Id] = actor, [target.Id] = target };
        var items = new Dictionary<string, Item> { [weapon.Id] = weapon };
        var context = CreateContext(session, characters, items);

        var rollService = Substitute.For<IRollService>();
        rollService.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RollOutcome { Result = 15, Summary = "Rolled 15" }));
        var selector = CreateSelector(rollService);
        var handler = new RulesetActionHandler(selector, _keys);

        var action = new RulesetAction
        {
            CharacterId = actor.Id,
            TargetIds = [target.Id],
            ActionType = RulesetActionType.Attack,
            ActionName = "Shortbow",
            Parameters = new Dictionary<string, string> { { "damageDice", "1d6" } }
        };

        var result = await handler.ApplyAsync(action, context, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ApplyAsync_RangeCheck_CaseInsensitiveBandStillBlocksOutOfRange()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Dnd5e);

        var actor = new Character
        {
            Id = "chars/actor",
            SystemStats = new Dnd5eExtension
            {
                // Lowercase band, mismatched casing vs. the SpatialDistanceBand constants.
                SpatialPositions = [new SpatialPosition { TargetId = "chars/target", DistanceBand = "distant" }]
            }
        };
        var target = new Character { Id = "chars/target", SystemStats = new Dnd5eExtension() };

        var characters = new Dictionary<string, Character> { [actor.Id] = actor, [target.Id] = target };
        var context = CreateContext(session, characters);

        var selector = CreateSelector(Substitute.For<IRollService>());
        var handler = new RulesetActionHandler(selector, _keys);

        var action = new RulesetAction
        {
            CharacterId = actor.Id,
            TargetIds = [target.Id],
            ActionType = RulesetActionType.Attack,
            ActionName = "Fist",
            // Mixed-case explicit range, should still match "Near" via case-insensitive comparison.
            Parameters = new Dictionary<string, string> { { "range", "nEaR" } }
        };

        var result = await handler.ApplyAsync(action, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("OutOfRange", result.Message);
    }

    [Fact]
    public async Task ApplyAsync_Pf2e_ActionEconomy_BlocksWhenOutOfActions()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await StoreConfigAsync(session, RulesetSystem.Pathfinder2e);

        var actor = new Character { Id = "chars/actor", SystemStats = new Pf2eExtension() };
        var characters = new Dictionary<string, Character> { [actor.Id] = actor };

        var activeCombat = new CombatEncounter
        {
            Id = _keys.CombatCurrent(_campaign),
            IsActive = true,
            ActiveTurnId = actor.Id,
            Combatants =
            [
                new CombatantState { CharacterId = actor.Id, ActionBudget = new Dictionary<string, int> { { "actions", 0 } }, ReactionAvailable = true }
            ]
        };

        var context = CreateContext(session, characters, activeCombat: activeCombat);
        var selector = CreateSelector(Substitute.For<IRollService>());
        var handler = new RulesetActionHandler(selector, _keys);

        var action = new RulesetAction
        {
            CharacterId = actor.Id,
            TargetIds = [],
            ActionType = RulesetActionType.SkillCheck,
            ActionName = "Athletics",
            Parameters = new Dictionary<string, string> { { "dc", "15" } }
        };

        var result = await handler.ApplyAsync(action, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("NoActionAvailable", result.Message);
    }

}
