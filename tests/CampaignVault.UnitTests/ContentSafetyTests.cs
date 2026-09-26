using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets.Modes;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Hooks content plugins rely on: LifeStage and its minor→adult ratchet, the mode entry veto,
/// mode_transition action=turn, and player-only campaign options.
/// </summary>
public class ContentSafetyTests
{
    [Theory]
    [InlineData(LifeStage.Unspecified, LifeStage.Adult, true, LifeStage.Adult)]
    [InlineData(LifeStage.Adult, LifeStage.Child, true, LifeStage.Child)]
    [InlineData(LifeStage.Adult, LifeStage.Unspecified, true, LifeStage.Adult)]
    [InlineData(LifeStage.Child, LifeStage.Adolescent, true, LifeStage.Adolescent)]
    [InlineData(LifeStage.Child, LifeStage.Adult, false, LifeStage.Child)]
    [InlineData(LifeStage.Adolescent, LifeStage.Elder, false, LifeStage.Adolescent)]
    public void LifeStageRules_TryChange_RatchetsMinorsOneWay(
        LifeStage current, LifeStage requested, bool allowed, LifeStage expected)
    {
        var ok = LifeStageRules.TryChange(current, requested, out var result, out var error);

        Assert.Equal(allowed, ok);
        Assert.Equal(expected, result);
        Assert.Equal(allowed, error is null);
    }

    [Fact]
    public void LifeStageRules_UnspecifiedIsNeitherAdultNorMinor()
    {
        Assert.False(LifeStageRules.IsAdult(LifeStage.Unspecified));
        Assert.False(LifeStageRules.IsMinor(LifeStage.Unspecified));
        Assert.True(LifeStageRules.IsAdult(LifeStage.Elder));
        Assert.True(LifeStageRules.IsMinor(LifeStage.Adolescent));
    }

    [Fact]
    public async Task CharacterUpdate_RefusesMinorToAdult_AndLeavesCharacterUntouched()
    {
        var character = new Character { Id = "chars/pip", Name = "Pip", LifeStage = LifeStage.Child, CurrentAppearance = "before" };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>("chars/pip", Arg.Any<CancellationToken>()).Returns(character);
        var handler = new CharacterUpdateHandler(new CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());
        var context = ChangeContextTestHelper.Create(session: session);

        var result = await handler.ApplyAsync(
            new CharacterUpdate { CharacterId = "chars/pip", LifeStage = LifeStage.Adult, AppearanceOverride = "after" },
            context);

        Assert.False(result.Success);
        Assert.Contains("cannot be changed", result.Message);
        Assert.Equal(LifeStage.Child, character.LifeStage);
        Assert.Equal("before", character.CurrentAppearance);
    }

    [Fact]
    public async Task CharacterUpdate_SetsLifeStage_WhenUnspecified()
    {
        var character = new Character { Id = "chars/mara", Name = "Mara" };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>("chars/mara", Arg.Any<CancellationToken>()).Returns(character);
        var handler = new CharacterUpdateHandler(new CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());

        var result = await handler.ApplyAsync(
            new CharacterUpdate { CharacterId = "chars/mara", LifeStage = LifeStage.Adult },
            ChangeContextTestHelper.Create(session: session));

        Assert.True(result.Success);
        Assert.Equal(LifeStage.Adult, character.LifeStage);
    }

    private sealed class ScriptedStateMachine(bool completeAfterTurn = false) : IModeStateMachine
    {
        public ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds) => new()
        {
            LocationId = locationId,
            Participants = participantIds.Select(id => new ModeParticipantState { CharacterId = id }).ToList(),
            ActiveTurnId = participantIds.FirstOrDefault(),
            Round = 1
        };

        public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant) => new Dictionary<string, int>();

        public bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason)
        {
            errorReason = null;
            return true;
        }

        public bool AdvanceTurn(ModeEncounter encounter)
        {
            var idx = encounter.Participants.FindIndex(p => p.CharacterId == encounter.ActiveTurnId);
            idx = (idx + 1) % encounter.Participants.Count;
            if (idx == 0)
            {
                encounter.Round++;
            }

            encounter.ActiveTurnId = encounter.Participants[idx].CharacterId;
            return true;
        }

        public bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative)
        {
            outcomeNarrative = completeAfterTurn ? "Scene over." : null;
            return completeAfterTurn;
        }
    }

    private sealed class VetoMode(string? veto, bool completeAfterTurn = false) : IInteractionMode
    {
        public string ModeId => "gated";
        public string DisplayName => "Gated";
        public IReadOnlyList<string> CompatibleSystems => [];
        public IModeStateMachine StateMachine { get; } = new ScriptedStateMachine(completeAfterTurn);
        public IReadOnlyDictionary<string, Character>? Seen { get; private set; }

        public string? ValidateEntry(
            IReadOnlyList<string> participantIds, IReadOnlyDictionary<string, Character> loaded, IChangeContext context)
        {
            Seen = loaded;
            return veto;
        }
    }

    private static IAsyncDocumentSession SessionWithoutEncounter()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<ModeEncounter>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ModeEncounter)null!);
        return session;
    }

    [Fact]
    public async Task ModeEnter_ValidateEntryVeto_FailsWithoutStoringEncounter()
    {
        var mode = new VetoMode("participant chars/pip is not an adult");
        var handler = new ModeTransitionChangeHandler(new InteractionModeSelector([mode]), new CampaignDocumentKeys());
        var session = SessionWithoutEncounter();
        var pip = new Character { Id = "chars/pip", Name = "Pip", LifeStage = LifeStage.Child };
        var context = ChangeContextTestHelper.Create(
            session: session,
            characters: new Dictionary<string, Character> { ["chars/pip"] = pip },
            campaignName: "test",
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["gated"] });

        var result = await handler.ApplyAsync(
            new ModeTransitionChange { ModeId = "gated", Action = "enter", LocationId = "locations/a", ParticipantIds = ["chars/pip", "chars/ghost"] },
            context);

        Assert.False(result.Success);
        Assert.Contains("not an adult", result.Message);
        Assert.Same(pip, mode.Seen!["chars/pip"]);
        Assert.False(mode.Seen.ContainsKey("chars/ghost"));
        await session.DidNotReceive().StoreAsync(Arg.Any<ModeEncounter>(), Arg.Any<CancellationToken>());
        Assert.Empty(context.ActiveModes);
    }

    [Fact]
    public async Task ModeTurn_AdvancesAndPublishesTurnStarted()
    {
        var mode = new VetoMode(null);
        var handler = new ModeTransitionChangeHandler(new InteractionModeSelector([mode]), new CampaignDocumentKeys());
        var encounter = new ModeEncounter
        {
            Id = new CampaignDocumentKeys().ModeCurrent("test", "gated"),
            ModeId = "gated",
            IsActive = true,
            Round = 1,
            ActiveTurnId = "chars/b",
            Participants = [new ModeParticipantState { CharacterId = "chars/a" }, new ModeParticipantState { CharacterId = "chars/b" }]
        };
        var context = ChangeContextTestHelper.Create(
            session: SessionWithoutEncounter(),
            campaignName: "test",
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["gated"] },
            activeModes: [encounter]);

        var result = await handler.ApplyAsync(new ModeTransitionChange { ModeId = "gated", Action = "turn" }, context);

        Assert.True(result.Success);
        Assert.Equal("chars/a", encounter.ActiveTurnId);
        Assert.Equal(2, encounter.Round);
        var pending = context.TakePendingEvents();
        Assert.True(pending.Count > 0, "no events published");
        var evt = Assert.Single(pending, e => e.Topic == CoreEvents.ModeTurnStarted);
        Assert.True(evt.TryGet<string>(CoreEvents.Fields.CharacterId, out var who));
        Assert.Equal("chars/a", who);
        Assert.True(evt.TryGet<bool>(CoreEvents.Fields.NewRound, out var newRound));
        Assert.True(newRound);
    }

    [Fact]
    public async Task ModeTurn_ExitsWhenStateMachineReportsComplete()
    {
        var mode = new VetoMode(null, completeAfterTurn: true);
        var handler = new ModeTransitionChangeHandler(new InteractionModeSelector([mode]), new CampaignDocumentKeys());
        var encounter = new ModeEncounter
        {
            Id = new CampaignDocumentKeys().ModeCurrent("test", "gated"),
            ModeId = "gated",
            IsActive = true,
            ActiveTurnId = "chars/a",
            Participants = [new ModeParticipantState { CharacterId = "chars/a" }]
        };
        var context = ChangeContextTestHelper.Create(
            session: SessionWithoutEncounter(),
            campaignName: "test",
            config: new CampaignConfig { Id = "campaigns/test/config", EnabledModeIds = ["gated"] },
            activeModes: [encounter]);

        var result = await handler.ApplyAsync(new ModeTransitionChange { ModeId = "gated", Action = "turn" }, context);

        Assert.True(result.Success);
        Assert.False(encounter.IsActive);
        var topics = context.TakePendingEvents().Select(e => e.Topic).ToList();
        Assert.Contains(CoreEvents.ModeExited, topics);
        Assert.DoesNotContain(CoreEvents.ModeTurnStarted, topics);
    }

    private static IReadOnlyList<PluginCampaignOption> PlayerOnlySchema() =>
    [
        new PluginCampaignOption { Key = "contentLimit", Type = "string", PlayerOnly = true },
        new PluginCampaignOption { Key = "flavor", Type = "string" }
    ];

    [Fact]
    public async Task CampaignUpdate_RefusesPlayerOnlyOption_WithoutPlayerRequest()
    {
        var saved = PluginDataRoots.DeclaredCampaignOptions;
        PluginDataRoots.DeclaredCampaignOptions = PlayerOnlySchema();
        try
        {
            var handler = new CampaignUpdateChangeHandler(new CampaignDocumentKeys());
            var config = new CampaignConfig { Id = "campaigns/test/config" };
            var context = ChangeContextTestHelper.Create(campaignName: "test", config: config);

            var result = await handler.ApplyAsync(
                new CampaignUpdateChange
                {
                    EnabledModeIds = ["crafting"],
                    SystemOptions = new Dictionary<string, string> { ["flavor"] = "x", ["CONTENTLIMIT"] = "none" }
                },
                context);

            Assert.False(result.Success);
            Assert.Contains("playerRequest", result.Message);
            Assert.Empty(config.EnabledModeIds);
        }
        finally
        {
            PluginDataRoots.DeclaredCampaignOptions = saved;
        }
    }

    [Fact]
    public async Task CampaignUpdate_RefusesPlayerOnlyOption_InsideLargerBatch()
    {
        var saved = PluginDataRoots.DeclaredCampaignOptions;
        PluginDataRoots.DeclaredCampaignOptions = PlayerOnlySchema();
        try
        {
            var handler = new CampaignUpdateChangeHandler(new CampaignDocumentKeys());
            var update = new CampaignUpdateChange
            {
                SystemOptions = new Dictionary<string, string> { ["contentLimit"] = "none" },
                PlayerRequest = "turn the limits off"
            };
            var context = ChangeContextTestHelper.Create(campaignName: "test", config: new CampaignConfig { Id = "campaigns/test/config" });
            context.Batch = [update, new HpChange { CharacterId = "chars/a", Delta = -1 }];

            var result = await handler.ApplyAsync(update, context);

            Assert.False(result.Success);
            Assert.Contains("own commit", result.Message);
        }
        finally
        {
            PluginDataRoots.DeclaredCampaignOptions = saved;
        }
    }
}
