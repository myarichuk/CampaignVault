using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 2c: a live-bound minion acts on its controller's turn through the ordinary
/// ruleset_action path — no new action type, no bypass of the other gates.
/// </summary>
[Collection("RavenDB")]
public class MinionTurnTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();
    private readonly string _campaign = "minion-turn-" + Guid.NewGuid().ToString("N")[..8];

    private static readonly Assembly Assembly = typeof(SpellDefinitionProvider).Assembly;

    public MinionTurnTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private RulesetActionHandler CreateHandler()
    {
        IRulesetModule[] modules =
        [
            new Dnd5eRulesetResolver(Substitute.For<IRollService>()),
            new Pf2eRulesetResolver(Substitute.For<IRollService>()),
            new NarrativeRulesetResolver(Substitute.For<IRollService>()),
        ];
        var dir = Path.Combine(Path.GetTempPath(), "cv_minion_turn_test_" + Guid.NewGuid());
        return new RulesetActionHandler(
            new RulesetModuleSelector(modules),
            _keys,
            new SpellDefinitionProvider(dir, Assembly),
            new FeatDefinitionProvider(dir, Assembly));
    }

    private ChangeContext CreateContext(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        Dictionary<string, Character> characters,
        CombatEncounter combat) =>
        new(
            session,
            characters,
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            new WorldChangeDispatcher([], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            combat,
            null,
            _campaign);

    private static CombatEncounter Combat(string activeTurnId, params string[] combatantIds) => new()
    {
        IsActive = true,
        Round = 2,
        ActiveTurnId = activeTurnId,
        Combatants = [.. combatantIds.Select(id => new CombatantState
        {
            CharacterId = id,
            ActionBudget = new Dictionary<string, int> { ["action"] = 1 },
            ReactionAvailable = true,
        })],
    };

    private static RulesetAction MinionProbe(string minionId) => new()
    {
        CharacterId = minionId,
        ActionType = RulesetActionType.SkillCheck,
        ActionName = "Perception",
        Parameters = new Dictionary<string, string>(),
    };

    private static Character BoundMinion(string id, string controllerId, bool lapsed = false) => new()
    {
        Id = id,
        Name = "Skeleton",
        CampaignName = "minion-turn",
        ControlledById = controllerId,
        MinionBinding = new MinionBinding { ControllerId = controllerId, ControlLapsed = lapsed },
        SystemStats = new Dnd5eExtension(),
    };

    [Fact]
    public async Task Minion_ActsOnControllersTurn()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var caster = new Character { Id = "chars/caster", Name = "Caster", SystemStats = new Dnd5eExtension() };
        var minion = BoundMinion("chars/minion", caster.Id);
        var combat = Combat(caster.Id, caster.Id, minion.Id);
        var context = CreateContext(session,
            new Dictionary<string, Character> { [caster.Id] = caster, [minion.Id] = minion }, combat);

        var result = await CreateHandler().ApplyAsync(MinionProbe(minion.Id), context, TestContext.Current.CancellationToken);

        // The probe itself fails (no dc) — but past the turn gate, which is the assertion.
        Assert.DoesNotContain("NotYourTurn", result.Message ?? string.Empty);
        var minionState = Assert.Single(combat.Combatants, c => c.CharacterId == minion.Id);
        Assert.Equal(0, minionState.ActionBudget["action"]);
        var casterState = Assert.Single(combat.Combatants, c => c.CharacterId == caster.Id);
        Assert.Equal(1, casterState.ActionBudget["action"]);
    }

    [Fact]
    public async Task Minion_BlockedOnAnotherTurn()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var caster = new Character { Id = "chars/caster", Name = "Caster", SystemStats = new Dnd5eExtension() };
        var rival = new Character { Id = "chars/rival", Name = "Rival", SystemStats = new Dnd5eExtension() };
        var minion = BoundMinion("chars/minion", caster.Id);
        var combat = Combat(rival.Id, caster.Id, rival.Id, minion.Id);
        var context = CreateContext(session,
            new Dictionary<string, Character> { [caster.Id] = caster, [rival.Id] = rival, [minion.Id] = minion }, combat);

        var result = await CreateHandler().ApplyAsync(MinionProbe(minion.Id), context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("NotYourTurn", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task Minion_LapsedBinding_BlockedOnControllersTurn()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var caster = new Character { Id = "chars/caster", Name = "Caster", SystemStats = new Dnd5eExtension() };
        var minion = BoundMinion("chars/minion", caster.Id, lapsed: true);
        var combat = Combat(caster.Id, caster.Id, minion.Id);
        var context = CreateContext(session,
            new Dictionary<string, Character> { [caster.Id] = caster, [minion.Id] = minion }, combat);

        var result = await CreateHandler().ApplyAsync(MinionProbe(minion.Id), context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("NotYourTurn", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task UnlinkedNpc_BlockedOnControllersTurn()
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(_campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var caster = new Character { Id = "chars/caster", Name = "Caster", SystemStats = new Dnd5eExtension() };
        var bystander = new Character { Id = "chars/bystander", Name = "Bystander", SystemStats = new Dnd5eExtension() };
        var combat = Combat(caster.Id, caster.Id, bystander.Id);
        var context = CreateContext(session,
            new Dictionary<string, Character> { [caster.Id] = caster, [bystander.Id] = bystander }, combat);

        var result = await CreateHandler().ApplyAsync(MinionProbe(bystander.Id), context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("NotYourTurn", result.Message ?? string.Empty);
    }
}
