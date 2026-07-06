using System.Collections.Generic;
using CampaignVault.Data;
using CampaignVault.Data.Initiative;
using CampaignVault.Data.Scenes;
using CampaignVault.Models;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class SceneAssemblerTests
{
    [Fact]
    public void SceneNpcMerger_PrefersSimulationState_And_FiltersByCampaign()
    {
        var merger = new SceneNpcMerger();
        var indexedNpc = new Character
        {
            Id = "chars/guard",
            Name = "Guard",
            CampaignName = "camp-a",
            CurrentActivity = "standing watch"
        };
        var simulatedNpc = new Character
        {
            Id = "chars/guard",
            Name = "Guard",
            CampaignName = "camp-a",
            CurrentActivity = "chasing a suspect"
        };
        var hiddenNpc = new Character
        {
            Id = "chars/hidden",
            Name = "Hidden",
            CampaignName = "camp-b"
        };
        var sharedNpc = new Character
        {
            Id = "chars/shared",
            Name = "Shared",
            CampaignName = null
        };

        var result = merger.Merge([indexedNpc, hiddenNpc], [simulatedNpc, sharedNpc], "camp-a");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, npc => npc.Id == "chars/guard" && npc.CurrentActivity == "chasing a suspect");
        Assert.Contains(result, npc => npc.Id == "chars/shared");
        Assert.DoesNotContain(result, npc => npc.Id == "chars/hidden");
    }

    [Fact]
    public void SceneNpcPresenceFactory_MergesDescriptors_And_UsesInitiativeEnrichment()
    {
        var behavior = Substitute.For<INpcBehaviorSynthesizer>();
        var initiative = Substitute.For<INpcInitiativeService>();
        var factory = new SceneNpcPresenceFactory(behavior, initiative);
        var npc = new Character
        {
            Id = "chars/alice",
            Name = "Alice",
            CurrentLocationId = "locations/inn",
            CurrentActivity = null,
            Psychology = new PsychologyProfile
            {
                CurrentMood = "Wary",
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Scene"] = new() { Topic = "Scene", Details = "Knows the room." }
                }
            },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float>
                {
                    ["hunger"] = 70f,
                    ["tiredness"] = 20f
                },
                NeedDescriptors = new Dictionary<string, string>
                {
                    ["hunger"] = "Personal hunger descriptor"
                }
            }
        };
        var sceneEvent = new Event { Id = "events/scene", Summary = "A loud argument", Involved = ["chars/alice"] };
        var campaignEvent = new Event { Id = "events/campaign", Summary = "Campaign echo", Involved = ["chars/alice"] };
        behavior.GenerateSummary(npc, Arg.Any<CampaignTime>(), Arg.Any<IEnumerable<Event>>()).Returns("Alice looks ready to bolt.");
        initiative.Enrich(Arg.Any<NpcInitiativeContext>(), Arg.Any<Campaign>()).Returns(new NpcInitiativeEnrichment(
            BehavioralTension: 42.5,
            TensionComponents: null,
            ActiveInitiatives: [new InitiativeCandidate("init:1", "chars/alice", InitiativeDriver.Memory, MemoryUrgency.High, "Speak up.", 0.9)],
            RelevantMemories: [new MemoryNode { Topic = "Debt", Details = "Owes money." }]));

        var result = factory.Create(new SceneNpcPresenceContext
        {
            PresentNpcs = [npc],
            Location = new Location { Id = "locations/inn", Name = "Inn" },
            RecentSceneEvents = [sceneEvent],
            RecentCampaignEvents = [campaignEvent],
            ItemsByHolder = new Dictionary<string, List<Item>>
            {
                ["chars/alice"] = [new Item { Id = "items/ring", Name = "Ring", HolderId = "chars/alice" }]
            },
            GlobalNeedDescriptors = new Dictionary<string, string>
            {
                ["hunger"] = "Global hunger descriptor",
                ["tiredness"] = "Global tiredness descriptor"
            },
            Time = new CampaignTime { TotalDaysElapsed = 12 },
            Config = new CampaignConfig(),
            Campaign = new Campaign { Id = "campaigns/camp-a/meta", Name = "camp-a", DisplayName = "Camp A" }
        });

        var summary = Assert.Single(result);
        Assert.Equal("Idle at default location", summary.CurrentActivity);
        Assert.Equal("Personal hunger descriptor", summary.NeedDescriptors["hunger"]);
        Assert.Equal("Global tiredness descriptor", summary.NeedDescriptors["tiredness"]);
        Assert.Equal(42.5, summary.BehavioralTension);
        Assert.Equal("Alice looks ready to bolt.", summary.BehavioralSummary);
        Assert.Single(summary.ActiveInitiatives!);
        Assert.NotNull(summary.HeldItems);
        Assert.Single(summary.HeldItems!);
        Assert.Equal("Ring", summary.HeldItems![0].Name);

        initiative.Received(1).Enrich(
            Arg.Is<NpcInitiativeContext>(ctx =>
                ctx.Npc.Id == "chars/alice"
                && ctx.CurrentDay == 12
                && ctx.NpcRecentEvents.Count == 1
                && ctx.NpcHeldItems.Count == 1
                && ctx.SurfacedViaTool == "get_scene"),
            Arg.Any<Campaign>());
    }

    [Fact]
    public void SceneFactionSummaryFactory_Computes_Reputation_And_LocalStance()
    {
        var factory = new SceneFactionSummaryFactory();
        var presentNpcs = new[]
        {
            new Character
            {
                Id = "chars/player",
                Name = "Player",
                Social = new SocialProfile
                {
                    FactionReputations = new Dictionary<string, int>
                    {
                        ["factions/guild"] = 12
                    }
                }
            }
        };
        var factions = new[]
        {
            new Faction
            {
                Id = "factions/guild",
                Name = "Guild",
                InfluenceLevel = 80,
                TerritoryLocationIds = ["locations/inn"],
                StanceToward = new Dictionary<string, FactionStance>
                {
                    ["factions/raiders"] = FactionStance.AtWar
                }
            },
            new Faction
            {
                Id = "factions/traders",
                Name = "Traders",
                InfluenceLevel = 50,
                TerritoryLocationIds = ["locations/inn"],
                StanceToward = new Dictionary<string, FactionStance>
                {
                    ["party"] = FactionStance.Opportunistic
                }
            },
            new Faction
            {
                Id = "factions/raiders",
                Name = "Raiders",
                InfluenceLevel = 60,
                TerritoryLocationIds = ["locations/inn"]
            }
        };

        var result = factory.Create(factions, presentNpcs);

        Assert.Contains(result, summary =>
            summary.FactionId == "factions/guild"
            && summary.PlayerReputation == 12
            && summary.LocalStance == FactionStance.AtWar);
        Assert.Contains(result, summary =>
            summary.FactionId == "factions/traders"
            && summary.LocalStance == FactionStance.Opportunistic);
    }

    [Fact]
    public void SceneAssembler_Assembles_View_And_MarksVisited()
    {
        var behavior = Substitute.For<INpcBehaviorSynthesizer>();
        var initiative = Substitute.For<INpcInitiativeService>();
        var assembler = new SceneAssembler(behavior, initiative);
        var location = new Location { Id = "locations/inn", Name = "Inn", LastVisitedDay = null };
        var npc = new Character
        {
            Id = "chars/guard",
            Name = "Guard",
            CampaignName = "camp-a",
            CurrentLocationId = "locations/inn",
            CurrentActivity = "watching the door"
        };
        behavior.GenerateSummary(Arg.Any<Character>(), Arg.Any<CampaignTime>(), Arg.Any<IEnumerable<Event>>()).Returns("Guard watches the door.");
        initiative.Enrich(Arg.Any<NpcInitiativeContext>(), Arg.Any<Campaign>()).Returns(new NpcInitiativeEnrichment(
            BehavioralTension: 5,
            TensionComponents: null,
            ActiveInitiatives: [],
            RelevantMemories: []));

        var combat = new CombatEncounter
        {
            Id = "campaigns/camp-a/combat/current",
            LocationId = "locations/inn",
            IsActive = true
        };

        var scene = assembler.Assemble(new SceneAssemblyContext
        {
            RequestedLocationId = "locations/inn",
            EffectiveCampaign = "camp-a",
            Location = location,
            NpcsFromIndex = [npc],
            NpcsFromSimulation = [npc],
            Rumors = [new Rumor { Id = "rumors/1", Subject = "Whispers", CurrentText = "Quiet talk", State = RumorState.Nascent }],
            Items = [new Item { Id = "items/lantern", Name = "Lantern", HolderId = "locations/inn" }],
            Events = [new Event { Id = "events/travel", Summary = "The party travel in after dusk.", Involved = ["locations/inn"] }],
            Time = new CampaignTime { TotalDaysElapsed = 9 },
            GlobalNeedDescriptors = new Dictionary<string, string>(),
            Config = new CampaignConfig(),
            Campaign = new Campaign { Id = "campaigns/camp-a/meta", Name = "camp-a", DisplayName = "Camp A" },
            RecentCampaignEvents = [],
            ItemsByHolder = new Dictionary<string, List<Item>>(),
            ActiveCombat = combat,
            ActiveQuests = [new Quest { Id = "quests/1", Title = "Keep Watch", RelatedLocationIds = ["locations/inn"] }],
            RelevantFactions = [],
            MarkVisited = true
        });

        Assert.True(scene.IsLocationAnchored);
        Assert.Equal(9, location.LastVisitedDay);
        Assert.Equal("The party travel in after dusk.", scene.LastKnownTravel);
        Assert.Same(combat, scene.ActiveCombat);
        Assert.Single(scene.PresentNPCs);
        Assert.Single(scene.LocalRumors);
        Assert.Single(scene.VisibleItems);
        Assert.Single(scene.ActiveQuests!);
    }
}
