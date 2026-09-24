using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Modes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Track A/B of PLUGIN_SYSTEM_PLAN.md: interaction-mode enablement scoping, mode-transition handling,
/// the IWorldChangeObserver post-commit hook tier, and the FindHandler fallback that makes
/// plugin-assembly-declared WorldChange subtypes actually dispatchable.
/// </summary>
public class InteractionModesTests
{
    private sealed class FakeModeStateMachine : IModeStateMachine
    {
        public ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds) => new()
        {
            LocationId = locationId,
            Participants = participantIds.Select(id => new ModeParticipantState { CharacterId = id }).ToList()
        };

        public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant) =>
            new Dictionary<string, int> { ["action"] = 1 };

        public bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason)
        {
            errorReason = null;
            return true;
        }

        public bool AdvanceTurn(ModeEncounter encounter) => encounter.IsActive;

        public bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative)
        {
            outcomeNarrative = null;
            return false;
        }
    }

    private sealed class FakeMode(
        string modeId,
        IReadOnlyList<string>? compatibleSystems = null,
        ModeParticipantClaim claim = ModeParticipantClaim.Independent) : IInteractionMode
    {
        public string ModeId { get; } = modeId;
        public string DisplayName => ModeId;
        public IReadOnlyList<string> CompatibleSystems { get; } = compatibleSystems ?? [];
        public IModeStateMachine StateMachine { get; } = new FakeModeStateMachine();
        public ModeParticipantClaim ParticipantClaim { get; } = claim;
    }

    [Fact]
    public void InteractionModeSelector_ResolvesRegisteredMode_ByModeId()
    {
        var selector = new InteractionModeSelector([new FakeMode("crafting"), new FakeMode("astral_combat")]);

        Assert.NotNull(selector.TryGetMode("crafting"));
        Assert.Null(selector.TryGetMode("hairstyling"));
        Assert.Equal(2, selector.RegisteredModeIds.Count);
    }

    private static IAsyncDocumentSession MockSessionWithNoExistingEncounter()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<ModeEncounter>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ModeEncounter)null!);
        return session;
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_Fails_WhenModeNotEnabledForCampaign()
    {
        var selector = new InteractionModeSelector([new FakeMode("crafting")]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var config = new CampaignConfig { Id = "campaigns/test/config" }; // EnabledModeIds empty

        var context = ChangeContextTestHelper.Create(
            session: MockSessionWithNoExistingEncounter(),
            campaignName: "test",
            config: config);

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = "crafting", Action = "enter", LocationId = "locations/forge", ParticipantIds = ["chars/pc1"] },
            context);

        Assert.False(result.Success);
        Assert.Contains("not enabled", result.Message);
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_Fails_WhenModeIncompatibleWithActiveSystem()
    {
        var selector = new InteractionModeSelector([new FakeMode("astral_combat", ["dnd5e"])]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var config = new CampaignConfig { Id = "campaigns/test/config", ActiveSystem = "pf2e", EnabledModeIds = ["astral_combat"] };

        var context = ChangeContextTestHelper.Create(
            session: MockSessionWithNoExistingEncounter(),
            campaignName: "test",
            config: config);

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = "astral_combat", Action = "enter", LocationId = "locations/astral", ParticipantIds = ["chars/pc1"] },
            context);

        Assert.False(result.Success);
        Assert.Contains("not compatible", result.Message);
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_Succeeds_WhenEnabledAndCompatible_AndStoresEncounter()
    {
        var selector = new InteractionModeSelector([new FakeMode("crafting")]); // system-agnostic
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var config = new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] };
        var session = MockSessionWithNoExistingEncounter();

        var context = ChangeContextTestHelper.Create(session: session, campaignName: "test", config: config);

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = "crafting", Action = "enter", LocationId = "locations/forge", ParticipantIds = ["chars/pc1"] },
            context);

        Assert.True(result.Success);
        Assert.NotNull(context.ActiveMode);
        Assert.Equal("crafting", context.ActiveMode.ModeId);
        Assert.True(context.ActiveMode.IsActive);
        await session.Received(1).StoreAsync(
            Arg.Is<ModeEncounter>(e => e.ModeId == "crafting" && e.IsActive && e.LocationId == "locations/forge"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_Fails_WhenModeUnknown()
    {
        var selector = new InteractionModeSelector([]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var context = ChangeContextTestHelper.Create(
            session: MockSessionWithNoExistingEncounter(),
            campaignName: "test",
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] });

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = "crafting", Action = "enter", LocationId = "locations/forge", ParticipantIds = ["chars/pc1"] },
            context);

        Assert.False(result.Success);
        Assert.Contains("Unknown interaction mode", result.Message);
    }

    [Fact]
    public async Task ModeTransitionHandler_Exit_Fails_WhenNoActiveEncounter()
    {
        var selector = new InteractionModeSelector([new FakeMode("crafting")]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var context = ChangeContextTestHelper.Create(
            session: MockSessionWithNoExistingEncounter(),
            campaignName: "test",
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] });

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = "crafting", Action = "exit" },
            context);

        Assert.False(result.Success);
        Assert.Contains("No active encounter", result.Message);
    }

    [Fact]
    public async Task ModeTransitionHandler_Exit_Succeeds_WhenEncounterActive()
    {
        var selector = new InteractionModeSelector([new FakeMode("crafting")]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var existing = new ModeEncounter { Id = "campaigns/test/mode/crafting/current", ModeId = "crafting", IsActive = true };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<ModeEncounter>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(existing);

        var context = ChangeContextTestHelper.Create(
            session: session,
            campaignName: "test",
            activeMode: existing,
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting"] });

        var result = await handler.ApplyAsync(new ModeTransitionChange { ModeId = "crafting", Action = "exit" }, context);

        Assert.True(result.Success);
        Assert.False(existing.IsActive);
        Assert.Null(context.ActiveMode);
    }

    private static ModeEncounter ActiveEncounter(string modeId, params string[] participantIds) => new()
    {
        Id = new CampaignDocumentKeys().ModeCurrent("test", modeId),
        ModeId = modeId,
        LocationId = "locations/pool",
        IsActive = true,
        Participants = participantIds.Select(id => new ModeParticipantState { CharacterId = id }).ToList()
    };

    private static async Task<(ChangeHandlerResult Result, ChangeContext Context)> EnterWhileActive(
        FakeMode entering, FakeMode alreadyActive, string participantId)
    {
        var selector = new InteractionModeSelector([entering, alreadyActive]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var config = new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = [entering.ModeId, alreadyActive.ModeId] };
        var context = ChangeContextTestHelper.Create(
            session: MockSessionWithNoExistingEncounter(), campaignName: "test", config: config,
            activeModes: [ActiveEncounter(alreadyActive.ModeId, "chars/aang")]);

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = entering.ModeId, Action = "enter", LocationId = "locations/pool", ParticipantIds = [participantId] },
            context);
        return (result, context);
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_AllowsOverlappingModes_WhenNeitherIsExclusive()
    {
        var (result, context) = await EnterWhileActive(new FakeMode("crafting"), new FakeMode("social"), "chars/aang");

        Assert.True(result.Success);
        Assert.Equal(["crafting", "social"], context.ActiveModes.Keys.Order());
        Assert.Equal("crafting", context.ActiveMode?.ModeId);
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_Fails_WhenParticipantHeldByExclusiveMode()
    {
        var (result, context) = await EnterWhileActive(
            new FakeMode("crafting"), new FakeMode("astral", claim: ModeParticipantClaim.Exclusive), "chars/aang");

        Assert.False(result.Success);
        Assert.Contains("astral", result.Message);
        Assert.DoesNotContain("crafting", context.ActiveModes.Keys);
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_Fails_WhenExclusiveModeClaimsBusyParticipant()
    {
        var (result, _) = await EnterWhileActive(
            new FakeMode("astral", claim: ModeParticipantClaim.Exclusive), new FakeMode("crafting"), "chars/aang");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ModeTransitionHandler_Enter_AllowsExclusiveMode_ForDifferentParticipants()
    {
        var (result, _) = await EnterWhileActive(
            new FakeMode("crafting"), new FakeMode("astral", claim: ModeParticipantClaim.Exclusive), "chars/sokka");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ModeTransitionHandler_Exit_KeepsOtherActiveModes()
    {
        var selector = new InteractionModeSelector([new FakeMode("crafting"), new FakeMode("astral")]);
        var handler = new ModeTransitionChangeHandler(selector, new CampaignDocumentKeys());
        var config = new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["crafting", "astral"] };
        var crafting = ActiveEncounter("crafting", "chars/aang");
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<ModeEncounter>(crafting.Id, Arg.Any<CancellationToken>()).Returns(crafting);
        var context = ChangeContextTestHelper.Create(
            session: session, campaignName: "test", config: config, activeMode: crafting,
            activeModes: [ActiveEncounter("astral", "chars/aang")]);

        var result = await handler.ApplyAsync(new ModeTransitionChange { ModeId = "crafting", Action = "exit" }, context);

        Assert.True(result.Success);
        Assert.Equal(["astral"], context.ActiveModes.Keys);
        Assert.Equal("astral", context.ActiveMode?.ModeId);
    }

    // --- Track A: CampaignConfig.EnabledModeIds write path (campaign_update) ---

    [Fact]
    public async Task CampaignUpdateHandler_SetsEnabledModeIds_OnPreloadedConfig()
    {
        var handler = new CampaignUpdateChangeHandler(new CampaignDocumentKeys());
        var config = new CampaignConfig { Id = "campaigns/test/config" };
        var context = ChangeContextTestHelper.Create(campaignName: "test", config: config);

        var result = await handler.ApplyAsync(
            new CampaignUpdateChange { EnabledModeIds = ["crafting", "astral_combat"] }, context);

        Assert.True(result.Success);
        Assert.Equal(["crafting", "astral_combat"], config.EnabledModeIds);
    }

    [Fact]
    public async Task CampaignUpdateHandler_CreatesAndStoresConfig_WhenNoneExistsYet()
    {
        var handler = new CampaignUpdateChangeHandler(new CampaignDocumentKeys());
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<CampaignConfig>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CampaignConfig)null!);
        var context = ChangeContextTestHelper.Create(session: session, campaignName: "test", config: null);

        var result = await handler.ApplyAsync(new CampaignUpdateChange { EnabledModeIds = ["crafting"] }, context);

        Assert.True(result.Success);
        await session.Received(1).StoreAsync(
            Arg.Is<CampaignConfig>(c => c.Id == "campaigns/test/config" && c.EnabledModeIds.Contains("crafting")),
            Arg.Any<CancellationToken>());
    }

    // --- IWorldChangeObserver tier ---

    private sealed class RecordingObserver : IWorldChangeObserver
    {
        public List<WorldChange> Observed { get; } = [];
        public bool IsInterestedIn(WorldChange committed, IChangeContext context) => true;
        public Task OnCommittedAsync(WorldChange committed, IChangeContext context, CancellationToken ct = default)
        {
            Observed.Add(committed);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IWorldChangeObserver
    {
        public bool IsInterestedIn(WorldChange committed, IChangeContext context) => true;
        public Task OnCommittedAsync(WorldChange committed, IChangeContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class InlineHandler(Func<WorldChange, bool> shouldHandle) : IWorldChangeHandler
    {
        public bool ShouldHandle(WorldChange change) => shouldHandle(change);
        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
            => Task.FromResult(ChangeHandlerResult.Ok);
    }

    [Fact]
    public async Task Dispatcher_NotifiesObserver_AfterSuccessfulCommit()
    {
        var observer = new RecordingObserver();
        var dispatcher = new WorldChangeDispatcher(
            [new InlineHandler(c => c is HpChange)],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance,
            observers: [observer]);

        var change = new HpChange { CharacterId = "chars/pc1", Delta = -1 };
        var result = await dispatcher.DispatchAsync(
            null!, [change], "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.Single(observer.Observed);
        Assert.Same(change, observer.Observed[0]);
    }

    [Fact]
    public async Task Dispatcher_ObserverException_IsSwallowed_AndDoesNotFailCommit()
    {
        var dispatcher = new WorldChangeDispatcher(
            [new InlineHandler(c => c is HpChange)],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance,
            observers: [new ThrowingObserver()]);

        var result = await dispatcher.DispatchAsync(
            null!, [new HpChange { CharacterId = "chars/pc1", Delta = -1 }], "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Dispatcher_DoesNotNotifyObserver_WhenHandlerFails()
    {
        var observer = new RecordingObserver();
        var dispatcher = new WorldChangeDispatcher(
            [new FailingInlineHandler()],
            new CampaignDocumentKeys(),
            NullLogger<WorldChangeDispatcher>.Instance,
            observers: [observer]);

        await dispatcher.DispatchAsync(
            null!, [new HpChange { CharacterId = "chars/pc1", Delta = -1 }], "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.Empty(observer.Observed);
    }

    private sealed class FailingInlineHandler : IWorldChangeHandler
    {
        public bool ShouldHandle(WorldChange change) => true;
        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
            => Task.FromResult(ChangeHandlerResult.Failure("nope"));
    }

    // --- FindHandler fallback for plugin-assembly-declared WorldChange subtypes ---

    /// <summary>
    /// Stands in for a WorldChange subtype declared in a *plugin* assembly (not typeof(WorldChange).Assembly),
    /// which BuildHandlerDictionary's core-assembly-only reflection scan never enumerates. FindHandler must
    /// still resolve it via the linear ShouldHandle fallback — this is what makes PLUGINS.md's "define your
    /// own WorldChange subtype, it Just Works" claim actually true for third-party plugin assemblies.
    /// </summary>
    private sealed class PluginDefinedChange : WorldChange
    {
        public string CharacterId { get; set; } = "chars/pc1";
    }

    [Fact]
    public async Task Dispatcher_FindHandler_FallsBackToLinearScan_ForChangeTypeOutsideCoreAssembly()
    {
        var handler = new InlineHandler(c => c is PluginDefinedChange);
        var dispatcher = new WorldChangeDispatcher(
            [handler], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            null!, [new PluginDefinedChange()], "test_campaign",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Summary, s => s.Contains("Unhandled change type"));
    }
}
