using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets.Modes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>SDK 0.8.0 host behavior: mode_transition join/leave, plugin <c>systems</c>, and engine-only plugin verbs.</summary>
public class PluginSystemsAndJoinTests
{
    [PluginWorldChange("sys_act", ModeId = "joinable")]
    public sealed class ActChange : WorldChange;

    [EngineOnly]
    [PluginWorldChange("sys_internal")]
    public sealed class InternalChange : WorldChange;

    [ActorAction]
    [PluginWorldChange("sys_do")]
    public sealed class DoChange : WorldChange
    {
        public string CharacterId { get; set; } = null!;
    }

    [PluginWorldChange("sys_save")]
    public sealed class SaveChange : WorldChange
    {
        public string CharacterId { get; set; } = null!;
    }

    private sealed class ActHandler : IWorldChangeHandler
    {
        public int Applied { get; private set; }
        public bool ShouldHandle(WorldChange change) => change is ActChange or InternalChange or DoChange or SaveChange;

        public bool ExtractInvolvedEntities(
            WorldChange change, HashSet<string>? characterIds = null, HashSet<string>? locationIds = null,
            HashSet<string>? factionIds = null, HashSet<string>? questIds = null, HashSet<string>? itemIds = null,
            HashSet<string>? allInvolvedIds = null)
        {
            var id = change switch { DoChange d => d.CharacterId, SaveChange v => v.CharacterId, _ => null };
            if (id is null) return false;
            characterIds?.Add(id);
            allInvolvedIds?.Add(id);
            return true;
        }

        public Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
        {
            Applied++;
            return Task.FromResult(ChangeHandlerResult.Ok);
        }
    }

    // Uses the interface's default TryAddParticipant / TryRemoveParticipant on purpose.
    private sealed class RoundMachine : IModeStateMachine
    {
        public ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds) => new()
        {
            LocationId = locationId,
            Participants = [.. participantIds.Select(id => new ModeParticipantState { CharacterId = id, ActionBudget = new() { ["action"] = 1 } })],
            ActiveTurnId = participantIds.FirstOrDefault()
        };

        public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant) => new Dictionary<string, int> { ["action"] = 1 };
        public bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason) { errorReason = null; return true; }
        public bool AdvanceTurn(ModeEncounter encounter) => true;
        public bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative) { outcomeNarrative = null; return false; }
    }

    private sealed class JoinableMode : IInteractionMode
    {
        public string ModeId => "joinable";
        public string DisplayName => "Joinable";
        public IReadOnlyList<string> CompatibleSystems => [];
        public IModeStateMachine StateMachine { get; } = new RoundMachine();
    }

    private static async Task<(CommitResult Result, ModeEncounter Encounter, ActHandler Handler)> DispatchAsync(
        string activeSystem, params WorldChange[] changes) => await DispatchAsync(activeSystem, [], changes);

    private static async Task<(CommitResult Result, ModeEncounter Encounter, ActHandler Handler)> DispatchAsync(
        string activeSystem, Character[] characters, params WorldChange[] changes)
    {
        var keys = new CampaignDocumentKeys();
        var mode = new JoinableMode();
        var encounter = mode.StateMachine.CreateEncounter("locations/a", ["chars/a", "chars/b"]);
        encounter.Id = keys.ModeCurrent("test", "joinable");
        encounter.ModeId = "joinable";
        encounter.IsActive = true;

        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(characters.ToDictionary(c => c.Id));
        session.LoadAsync<Item>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Item>());
        session.LoadAsync<Location>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Location>());
        session.LoadAsync<CampaignConfig>(Arg.Any<string>())
            .Returns(new CampaignConfig { Id = keys.Config("test"), EnabledModeIds = ["joinable"], ActiveSystem = activeSystem });
        session.LoadAsync<ModeEncounter>(Arg.Any<string>()).Returns(encounter);

        var handler = new ActHandler();
        var selector = new InteractionModeSelector([mode]);
        var dispatcher = new WorldChangeDispatcher(
            [handler, new ModeTransitionChangeHandler(selector, keys)], keys, NullLogger<WorldChangeDispatcher>.Instance,
            modeSelector: selector);

        var result = await dispatcher.DispatchAsync(
            session, changes, "test",
            () => Task.FromResult(new CampaignTime()),
            () => Task.FromResult(new Dictionary<string, string>()),
            _ => Task.CompletedTask);
        return (result, encounter, handler);
    }

    private static ModeTransitionChange Transition(string action, params string[] ids) =>
        new() { ModeId = "joinable", Action = action, ParticipantIds = [.. ids] };

    [Fact]
    public async Task Join_AppendsTheNewcomerWithNoActions_AndPublishesNothingElse()
    {
        var (result, encounter, _) = await DispatchAsync("dnd5e", Transition("join", "chars/c"));

        Assert.True(result.Success, string.Join("; ", result.Summary));
        Assert.Equal(["chars/a", "chars/b", "chars/c"], encounter.Participants.Select(p => p.CharacterId));
        Assert.Equal(0, encounter.Participants[2].ActionBudget["action"]);
        Assert.Equal("chars/a", encounter.ActiveTurnId);
    }

    [Fact]
    public async Task Join_RefusesSomeoneAlreadyInTheScene()
    {
        var (result, encounter, _) = await DispatchAsync("dnd5e", Transition("join", "chars/b"));

        Assert.False(result.Success);
        Assert.Equal(2, encounter.Participants.Count);
    }

    [Fact]
    public async Task Leave_PassesTheTurnOn_AndTheLastOneOutMustExit()
    {
        var (result, encounter, _) = await DispatchAsync("dnd5e", Transition("leave", "chars/a"));

        Assert.True(result.Success, string.Join("; ", result.Summary));
        Assert.Equal(["chars/b"], encounter.Participants.Select(p => p.CharacterId));
        Assert.Equal("chars/b", encounter.ActiveTurnId);

        var (emptied, kept, _) = await DispatchAsync("dnd5e", Transition("leave", "chars/a", "chars/b"));
        Assert.False(emptied.Success);
        Assert.Equal(2, kept.Participants.Count);
    }

    [Fact]
    public async Task PluginSystems_RefusesAVerbOutsideItsSystems_AndAllowsItInside()
    {
        PluginSystems.Set([(typeof(PluginSystemsAndJoinTests).Assembly, ["dnd5e"])]);
        try
        {
            var (allowed, _, allowedHandler) = await DispatchAsync("DND5E", new ActChange());
            Assert.True(allowed.Success, string.Join("; ", allowed.Summary));
            Assert.Equal(1, allowedHandler.Applied);

            var (refused, _, refusedHandler) = await DispatchAsync("narrative", new ActChange());
            Assert.False(refused.Success);
            Assert.Equal(0, refusedHandler.Applied);
            Assert.Contains(refused.Summary, s => s.Contains("applies only to dnd5e"));
        }
        finally
        {
            PluginSystems.Set([]);
        }
    }

    [Fact]
    public async Task EngineOnlyPluginVerb_IsRefusedWhenTheModelSendsIt()
    {
        var (result, _, handler) = await DispatchAsync("dnd5e", new InternalChange());

        Assert.False(result.Success);
        Assert.Equal(0, handler.Applied);
        Assert.Contains(result.Summary, s => s.Contains("emitted by the engine"));
    }

    private static Character Bearer(string name, string? appliedBy = null, Dictionary<string, float>? mods = null)
    {
        var c = new Character { Id = "chars/a", Name = "Mara" };
        c.SystemStats.StatusEffects.Add(new StatusEffect { Name = name, AppliedBy = appliedBy, StatModifiers = mods ?? [] });
        return c;
    }

    [Theory]
    [InlineData("incapacitated")]
    [InlineData("Stunned")]
    [InlineData("paralyzed")]
    public async Task ActorAction_IsRefusedWhileTheActorIsBlocked_ButSavesAreNot(string condition)
    {
        var (acted, _, handler) = await DispatchAsync("dnd5e", [Bearer(condition, "lewd")], new DoChange { CharacterId = "chars/a" });
        Assert.False(acted.Success);
        Assert.Equal(0, handler.Applied);
        Assert.Contains(acted.Summary, s => s.Contains("cannot act") && s.Contains("lewd"));

        var (saved, _, saveHandler) = await DispatchAsync("dnd5e", [Bearer(condition)], new SaveChange { CharacterId = "chars/a" });
        Assert.True(saved.Success, string.Join("; ", saved.Summary));
        Assert.Equal(1, saveHandler.Applied);
    }

    [Fact]
    public async Task ActorAction_IsRefusedByThePluginTag_AndAllowedOnceTheStatusIsGone()
    {
        var tagged = Bearer("Flustered", "somewhere", new() { [ActionBlock.Tag] = 1 });
        var (refused, _, _) = await DispatchAsync("dnd5e", [tagged], new DoChange { CharacterId = "chars/a" });
        Assert.False(refused.Success);

        tagged.SystemStats.StatusEffects.Clear();
        var (allowed, _, handler) = await DispatchAsync("dnd5e", [tagged], new DoChange { CharacterId = "chars/a" });
        Assert.True(allowed.Success, string.Join("; ", allowed.Summary));
        Assert.Equal(1, handler.Applied);
    }

    [Fact]
    public async Task ActorAction_ResumesOnceATimedBlockHasExpired_WithNothingRemovingIt()
    {
        // The test clock stands at day 0, hour 6 → 0.25.
        var expired = Bearer("Stunned", "lewd");
        expired.SystemStats.StatusEffects[0].ExpiresAtDay = 0.2f;
        var (allowed, _, handler) = await DispatchAsync("dnd5e", [expired], new DoChange { CharacterId = "chars/a" });
        Assert.True(allowed.Success, string.Join("; ", allowed.Summary));
        Assert.Equal(1, handler.Applied);

        var pending = Bearer("Stunned", "lewd");
        pending.SystemStats.StatusEffects[0].ExpiresAtDay = 0.3f;
        var (refused, _, _) = await DispatchAsync("dnd5e", [pending], new DoChange { CharacterId = "chars/a" });
        Assert.False(refused.Success);
    }

    [Fact]
    public void ActionBlock_RecognisesBlockingConditionsAndIgnoresTheRest()
    {
        Assert.NotNull(ActionBlock.Blocker(Bearer("unconscious")));
        Assert.Null(ActionBlock.Blocker(Bearer("prone")));
        Assert.Null(ActionBlock.Blocker(Bearer("Flustered", mods: new() { [ActionBlock.Tag] = 0 })));
        Assert.Null(ActionBlock.Blocker(null));
    }
}
