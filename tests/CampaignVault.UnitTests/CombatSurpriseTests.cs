using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SRD 5.1 surprise: a surprised combatant can't move or act on its first turn and can't react until that turn ends.
/// </summary>
[Collection("RavenDB")]
public class CombatSurpriseTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public CombatSurpriseTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(CampaignTools Tools, string Campaign, string Rogue, string Goblin)> SeedAsync(string system = RulesetSystem.Dnd5e)
    {
        var campaign = "camp_" + Guid.NewGuid();
        var rogue = "chars/rogue_" + Guid.NewGuid();
        var goblin = "chars/goblin_" + Guid.NewGuid();
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(campaign), ActiveSystem = system }, TestContext.Current.CancellationToken);
        await session.StoreAsync(new Character { Id = rogue, Name = "Rogue", CurrentHp = 10, MaxHp = 10, IsPc = true, CampaignName = campaign, SystemStats = new Dnd5eExtension() }, TestContext.Current.CancellationToken);
        await session.StoreAsync(new Character { Id = goblin, Name = "Goblin", CurrentHp = 7, MaxHp = 7, CampaignName = campaign, SystemStats = new Dnd5eExtension() }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (TestCampaignToolsFactory.Create(_fixture), campaign, rogue, goblin);
    }

    [Fact]
    public async Task StartCombat_SurprisedCombatant_LosesReactionAndFirstTurn()
    {
        var (tools, campaign, rogue, goblin) = await SeedAsync();

        var start = await tools.StartCombat("loc_" + Guid.NewGuid(), [rogue, goblin], campaign, surprisedIds: [goblin]);

        Assert.True(start.Success, start.Summary);
        var state = start.Data!.Combatants.Single(c => c.CharacterId == goblin);
        Assert.True(state.Surprised);
        Assert.False(state.ReactionAvailable);
        Assert.False(start.Data.Combatants.Single(c => c.CharacterId == rogue).Surprised);
        Assert.Contains("Surprised", start.Summary);

        // Walk the goblin's first turn: on it every action is spent; once it ends, surprise and the reaction are back.
        var turn = start.Data;
        if (turn.ActiveTurnId != goblin)
        {
            turn = (await tools.NextTurn(campaignName: campaign)).Data!;
        }

        Assert.Equal(goblin, turn.ActiveTurnId);
        var onTurn = turn.Combatants.Single(c => c.CharacterId == goblin);
        Assert.True(onTurn.Surprised);
        Assert.All(onTurn.ActionBudget.Values, v => Assert.Equal(0, v));

        var after = await tools.NextTurn(campaignName: campaign);
        var cleared = after.Data!.Combatants.Single(c => c.CharacterId == goblin);
        Assert.False(cleared.Surprised);
        Assert.True(cleared.ReactionAvailable);
    }

    [Fact]
    public async Task StartCombat_SurprisedIdNotACombatant_IsRejected()
    {
        var (tools, campaign, rogue, goblin) = await SeedAsync();

        var start = await tools.StartCombat("loc_" + Guid.NewGuid(), [rogue], campaign, surprisedIds: [goblin]);

        Assert.False(start.Success);
        Assert.Contains("must also be in combatantIds", start.Summary);
    }

    [Fact]
    public async Task StartCombat_SurpriseInPf2e_IsRejected()
    {
        var (tools, campaign, rogue, goblin) = await SeedAsync(RulesetSystem.Pathfinder2e);

        var start = await tools.StartCombat("loc_" + Guid.NewGuid(), [rogue, goblin], campaign, surprisedIds: [goblin]);

        Assert.False(start.Success);
        Assert.Contains("D&D 5e surprise rule", start.Summary);
    }

    [Fact]
    public async Task Handler_SurprisedCombatant_CannotAct()
    {
        var campaign = "camp_" + Guid.NewGuid();
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new CampaignConfig { Id = _keys.Config(campaign), ActiveSystem = RulesetSystem.Dnd5e }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var goblin = new Character { Id = "chars/goblin", Name = "Goblin", CurrentHp = 7, SystemStats = new Dnd5eExtension() };
        var rogue = new Character { Id = "chars/rogue", Name = "Rogue", CurrentHp = 10, SystemStats = new Dnd5eExtension() };
        var combat = new CombatEncounter
        {
            Id = _keys.CombatCurrent(campaign),
            IsActive = true,
            ActiveTurnId = goblin.Id,
            Combatants =
            [
                new CombatantState { CharacterId = goblin.Id, Surprised = true, ReactionAvailable = false, ActionBudget = new() { ["action"] = 0 } },
                new CombatantState { CharacterId = rogue.Id, ActionBudget = new() { ["action"] = 1 } },
            ],
        };
        var context = new ChangeContext(
            session,
            new Dictionary<string, Character> { [goblin.Id] = goblin, [rogue.Id] = rogue },
            new Dictionary<string, Item>(),
            new Dictionary<string, Location>(),
            new Dictionary<string, Faction>(),
            new Dictionary<string, Quest>(),
            NullLogger.Instance,
            [],
            new WorldChangeDispatcher([], _keys, NullLogger<WorldChangeDispatcher>.Instance),
            combat,
            null,
            campaign);
        IRulesetModule[] modules = [new Dnd5eRulesetResolver(Substitute.For<IRollService>())];
        var assembly = typeof(SpellDefinitionProvider).Assembly;
        var handler = new RulesetActionHandler(new RulesetModuleSelector(modules), _keys,
            new SpellDefinitionProvider(Path.Combine(Path.GetTempPath(), "cv_surprise_spell_" + Guid.NewGuid()), assembly),
            new FeatDefinitionProvider(Path.Combine(Path.GetTempPath(), "cv_surprise_feat_" + Guid.NewGuid()), assembly));

        var result = await handler.ApplyAsync(new RulesetAction
        {
            CharacterId = goblin.Id,
            TargetIds = [rogue.Id],
            ActionType = RulesetActionType.Attack,
            ActionName = "Scimitar",
            Parameters = new() { ["damageDice"] = "1d6" },
        }, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("[Surprised]", result.Message);
    }
}
