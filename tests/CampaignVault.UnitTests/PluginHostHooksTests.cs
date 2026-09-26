using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Context;
using CampaignVault.Data.Migrations;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets.Modes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SDK 0.7.0 host hooks: campaign-option upgraders, the mode action budget, player-only modes, and character
/// lookup on the context turn.
/// </summary>
[Collection("RavenDB")]
public class PluginHostHooksTests(RavenDBFixture fixture) : IClassFixture<RavenDBFixture>
{
    private sealed class RenameUpgrader : IPluginCampaignOptionsUpgrader
    {
        public string PluginId => "test.plugin";

        public bool TryUpgrade(IDictionary<string, string> systemOptions)
        {
            var key = systemOptions.Keys.FirstOrDefault(k => string.Equals(k, "hooksOldOpt", StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                return false;
            }

            if (!systemOptions.ContainsKey("hooksNewOpt"))
            {
                systemOptions["hooksNewOpt"] = systemOptions[key].ToUpperInvariant();
            }

            systemOptions.Remove(key);
            return true;
        }
    }

    private sealed class ThrowingUpgrader : IPluginCampaignOptionsUpgrader
    {
        public string PluginId => "test.broken";
        public bool TryUpgrade(IDictionary<string, string> systemOptions) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void OptionsUpgrade_Apply_SkipsAThrowingUpgrader()
    {
        var options = new Dictionary<string, string> { ["HOOKSOLDOPT"] = "grim" };

        var changed = UpgradePluginCampaignOptions.Apply(options, [new ThrowingUpgrader(), new RenameUpgrader()]);

        Assert.True(changed);
        Assert.Equal("GRIM", options["hooksNewOpt"]);
        Assert.False(options.ContainsKey("HOOKSOLDOPT"));
    }

    [Fact]
    public async Task OptionsUpgrade_Execute_RewritesMetaAndConfig_AndIsIdempotent()
    {
        var keys = new CampaignDocumentKeys();
        var name = $"hooks-{Guid.NewGuid():N}";
        using (var session = fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta(name), Name = name, SystemOptions = new() { ["hooksOldOpt"] = "fade" } });
            await session.StoreAsync(new CampaignConfig
            {
                Id = keys.Config(name),
                SystemOptions = new() { ["hooksOldOpt"] = "fade", ["hooksNewOpt"] = "player-set" }
            });
            await session.SaveChangesAsync();
        }

        var migration = new UpgradePluginCampaignOptions(fixture.Store, [new RenameUpgrader()]);
        Assert.True(await migration.ExecuteAsync() >= 2);
        Assert.Equal(0, await migration.ExecuteAsync());

        using (var session = fixture.Store.OpenAsyncSession())
        {
            var meta = await session.LoadAsync<Campaign>(keys.Meta(name));
            var config = await session.LoadAsync<CampaignConfig>(keys.Config(name));
            Assert.Equal("FADE", meta.SystemOptions["hooksNewOpt"]);
            Assert.False(meta.SystemOptions.ContainsKey("hooksOldOpt"));
            Assert.Equal("player-set", config.SystemOptions["hooksNewOpt"]); // never overwrites the new key
            Assert.False(config.SystemOptions.ContainsKey("hooksOldOpt"));
        }
    }

    [PluginWorldChange("hooks_act", ModeId = "budgeted")]
    public sealed class ActChange : WorldChange
    {
        public string ActorId { get; set; } = "";
        public bool Fail { get; set; }
    }

    private sealed class ActHandler : IWorldChangeHandler
    {
        public int Applied { get; private set; }
        public bool ShouldHandle(WorldChange change) => change is ActChange;

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
        {
            if (((ActChange)change).Fail)
            {
                return Task.FromResult(ChangeHandlerResult.Failure("the act itself failed"));
            }

            Applied++;
            return Task.FromResult(ChangeHandlerResult.Ok);
        }
    }

    private sealed class OneActionMachine : IModeStateMachine
    {
        public ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds) => new()
        {
            Participants = participantIds
                .Select(id => new ModeParticipantState { CharacterId = id, ActionBudget = new() { ["action"] = 1 } })
                .ToList(),
            ActiveTurnId = participantIds.FirstOrDefault()
        };

        public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant) => new Dictionary<string, int> { ["action"] = 1 };

        public bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason)
        {
            errorReason = null;
            if (state.ActionBudget.GetValueOrDefault("action") <= 0)
            {
                errorReason = "No action left.";
                return false;
            }

            state.ActionBudget["action"]--;
            return true;
        }

        public bool AdvanceTurn(ModeEncounter encounter) => true;

        public bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative)
        {
            outcomeNarrative = null;
            return false;
        }
    }

    private sealed class BudgetedMode : IInteractionMode
    {
        public string ModeId => "budgeted";
        public string DisplayName => "Budgeted";
        public IReadOnlyList<string> CompatibleSystems => [];
        public IModeStateMachine StateMachine { get; } = new OneActionMachine();
    }

    private static async Task<(CommitResult Result, ModeEncounter Encounter, ActHandler Handler)> DispatchActsAsync(params ActChange[] acts)
    {
        var keys = new CampaignDocumentKeys();
        var mode = new BudgetedMode();
        var encounter = mode.StateMachine.CreateEncounter("locations/a", ["chars/a", "chars/b"]);
        encounter.Id = keys.ModeCurrent("test", "budgeted");
        encounter.ModeId = "budgeted";
        encounter.IsActive = true;

        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Character>());
        session.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Item>());
        session.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Location>());
        session.LoadAsync<CampaignConfig>(Arg.Any<string>())
            .Returns(new CampaignConfig { Id = keys.Config("test"), EnabledModeIds = ["budgeted"] });
        session.LoadAsync<ModeEncounter>(Arg.Any<string>()).Returns(encounter);

        var handler = new ActHandler();
        var dispatcher = new WorldChangeDispatcher(
            [handler], keys, NullLogger<WorldChangeDispatcher>.Instance,
            modeSelector: new InteractionModeSelector([mode]));

        var result = await dispatcher.DispatchAsync(
            session, acts.Cast<WorldChange>().ToArray(), "test",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);
        return (result, encounter, handler);
    }

    [Fact]
    public async Task ModeVerb_SpendsTheActorsSlot_AndASecondActIsRefused()
    {
        var (result, encounter, handler) = await DispatchActsAsync(
            new ActChange { ActorId = "chars/b" }, new ActChange { ActorId = "chars/b" });

        Assert.False(result.Success);
        Assert.Equal(1, handler.Applied);
        Assert.Contains(result.Summary, s => s.Contains("mode_transition action=turn"));
        Assert.Equal(0, encounter.Participants[1].ActionBudget["action"]);
        Assert.Equal(1, encounter.Participants[0].ActionBudget["action"]); // chars/a untouched
    }

    [Fact]
    public async Task ModeVerb_ThatFails_GetsItsSlotBack()
    {
        var (_, encounter, _) = await DispatchActsAsync(new ActChange { ActorId = "chars/a", Fail = true });

        Assert.Equal(1, encounter.Participants[0].ActionBudget["action"]);
    }

    [Fact]
    public async Task ModeVerb_WithoutActor_ChargesWhoeversTurnItIs()
    {
        var (result, encounter, _) = await DispatchActsAsync(new ActChange());

        Assert.True(result.Success, string.Join("; ", result.Summary));
        Assert.Equal(0, encounter.Participants[0].ActionBudget["action"]);
    }

    private static async Task<ChangeHandlerResult> UpdateModesAsync(CampaignConfig config, CampaignUpdateChange update, int batchSize = 1)
    {
        var saved = PluginDataRoots.PlayerOnlyModeIds;
        PluginDataRoots.PlayerOnlyModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "private_mode" };
        try
        {
            var context = ChangeContextTestHelper.Create(campaignName: "test", config: config);
            context.Batch = Enumerable.Range(0, batchSize).Select(i => i == 0 ? (WorldChange)update : new HpChange { CharacterId = "chars/a" }).ToList();
            return await new CampaignUpdateChangeHandler(new CampaignDocumentKeys()).ApplyAsync(update, context);
        }
        finally
        {
            PluginDataRoots.PlayerOnlyModeIds = saved;
        }
    }

    [Fact]
    public async Task CampaignUpdate_PlayerOnlyMode_NeedsThePlayersWords()
    {
        var config = new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] };

        var refused = await UpdateModesAsync(config, new CampaignUpdateChange { EnabledModeIds = ["crafting", "PRIVATE_MODE"] });
        Assert.False(refused.Success);
        Assert.Contains("mode private_mode", refused.Message);
        Assert.Equal(["crafting"], config.EnabledModeIds);

        var batched = await UpdateModesAsync(
            config, new CampaignUpdateChange { EnabledModeIds = ["crafting", "private_mode"], PlayerRequest = "turn it on" }, batchSize: 2);
        Assert.False(batched.Success);

        var ok = await UpdateModesAsync(
            config, new CampaignUpdateChange { EnabledModeIds = ["crafting", "private_mode"], PlayerRequest = "turn it on" });
        Assert.True(ok.Success);
        Assert.Contains("private_mode", config.EnabledModeIds);
    }

    [Fact]
    public async Task CampaignUpdate_OtherModes_StayFree_WhileAPlayerOnlyModeIsUntouched()
    {
        var config = new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["private_mode"] };

        var result = await UpdateModesAsync(config, new CampaignUpdateChange { EnabledModeIds = ["private_mode", "crafting"] }, batchSize: 3);

        Assert.True(result.Success);
        Assert.Equal(["private_mode", "crafting"], config.EnabledModeIds);
    }

    [Fact]
    public async Task ContextTurn_LoadCharacter_ReadsPartyThenSession()
    {
        var pc = new Character { Id = "chars/pc", Name = "PC", IsPc = true };
        var npc = new Character { Id = "chars/npc", Name = "NPC" };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>("chars/npc", Arg.Any<CancellationToken>()).Returns(npc);
        IContextTurn turn = new ContextTurn
        {
            Session = session,
            CampaignName = "test",
            Config = new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] },
            AppliedChanges = [],
            InvolvedEntityIds = ["chars/npc"],
            Party = [pc]
        };

        Assert.Same(pc, await turn.LoadCharacterAsync("CHARS/PC"));
        Assert.Same(npc, await turn.LoadCharacterAsync("chars/npc"));
        Assert.Null(await turn.LoadCharacterAsync("chars/nobody"));
        Assert.Equal(["crafting"], turn.Config!.EnabledModeIds);
        session.Advanced.Received().Evict(npc);
    }

    private static CampaignConfig MeterSchemas() => new()
    {
        ResourcePoolSchemas = new()
        {
            ["meter"] = new ResourcePoolTemplate { DefaultMax = 10, Recovery = RecoveryType.Never, OwnerManaged = true, StartsAt = "zero" },
            ["buffer"] = new ResourcePoolTemplate { DefaultMax = 0, Recovery = RecoveryType.Never, OwnerManaged = true },
            ["plain"] = new ResourcePoolTemplate { DefaultMax = 3, Recovery = RecoveryType.LongRest },
        }
    };

    [Fact]
    public void PoolInitializer_CreatesOwnerManagedPoolsOnce_AndThenLeavesThemAlone()
    {
        var character = new Character { Id = "chars/a", SystemStats = new Dnd5eExtension { Level = 1 } };
        var initializer = new CampaignVault.Services.ResourcePoolInitializer();

        initializer.InitializePools(character, RulesetSystem.Dnd5e, MeterSchemas());
        var pools = character.SystemStats.ResourcePools;
        Assert.Equal(0, pools["meter"].Current); // starts empty
        Assert.Equal(10, pools["meter"].Max);
        Assert.True(pools.ContainsKey("buffer")); // kept even at max 0
        Assert.Equal(3, pools["plain"].Current);

        pools["meter"] = pools["meter"] with { Max = 23, Current = 15 };
        pools["buffer"] = pools["buffer"] with { Max = 6, Current = 6 };
        initializer.InitializePools(character, RulesetSystem.Dnd5e, MeterSchemas());

        Assert.Equal(23, character.SystemStats.ResourcePools["meter"].Max);
        Assert.Equal(15, character.SystemStats.ResourcePools["meter"].Current);
        Assert.Equal(6, character.SystemStats.ResourcePools["buffer"].Current);
    }
}
