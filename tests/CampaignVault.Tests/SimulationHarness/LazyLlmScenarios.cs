using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests.SimulationHarness;

[Collection("RavenDB")]
public class LazyLlmScenarios : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public LazyLlmScenarios(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
        new Location_Search().Execute(_store);
        new Character_Search().Execute(_store);
    }

    [Fact]
    public async Task GetScene_EmptyFlavorVacuum_ProducesNarrativePrompt()
    {
        var repo = _fixture.CreateRepository();
        using (var session = _store.OpenAsyncSession())
        {
            var loc = new Location
            {
                Id = "locations/empty-room-" + Guid.NewGuid(),
                Name = "Empty Room",
                Description = "A completely bare room.",
                Type = LocationType.Room,
                CampaignName = "default"
            };
            await repo.UpsertLocationAsync(session, loc, "default");
            await session.SaveChangesAsync();

            var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

            var result = await tools.GetScene(loc.Id, partyPresent: true, campaignName: "default");

            Assert.True(result.Success);
            var pressures = result.WorldPressure;
            Assert.NotNull(pressures);
            Assert.Contains(pressures, p => p.Contains("lacks flavor"));
        }
    }

    [Fact]
    public async Task GetScene_MisspelledLocation_ProvidesSuggestions()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta("LenientTest"), Name = "LenientTest" });

            var loc = new Location
            {
                Id = "locations/pony-" + Guid.NewGuid(),
                Name = "The Prancing Pony",
                Description = "A well-known inn.",
                Type = LocationType.Room,
                CampaignName = "LenientTest"
            };
            await repo.UpsertLocationAsync(session, loc, "LenientTest");
            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene("Prancing Pony", false, "LenientTest");

        Assert.True(result.Success);
        var view = result.Data;
        Assert.NotNull(view);
        Assert.False(view.IsLocationAnchored); // Hallucinated ID

        var pressures = result.WorldPressure;
        Assert.NotNull(pressures);
        Assert.Contains(pressures, p => p.Contains("Did you mean one of these:"));
        Assert.Contains(pressures, p => p.Contains("(The Prancing Pony)"));
    }

    [Fact]
    public async Task Commit_MisspelledCharacter_ProvidesSuggestions()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("CharacterLenientTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("CharacterLenientTest"), Name = "CharacterLenientTest" });

            var character = new Character
            {
                Id = "chars/drizzzt",
                Name = "Drizzt Do'Urden",
                CampaignName = "CharacterLenientTest",
                CurrentHp = 10,
                MaxHp = 10
            };
            await session.StoreAsync(character);
            await session.SaveChangesAsync();

            // Wait for index
            await session.Query<Character, Character_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .ToListAsync();
        }

        var changes = new WorldChange[]
        {
            new HpChange { CharacterId = "chars/drizz", Delta = -5 }
        };

        var result = await tools.Commit(changes, "Attack hits");

        Assert.False(result.Success);
        Assert.Contains("Did you mean: chars/drizzzt (Drizzt Do'Urden)?", result.Error);
    }

    [Fact]
    public async Task LLM_Forgets_To_Arrive_Produces_TravelInterruptedPressure_And_Resolves_On_Commit()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("TravelLazinessTest");

        var charId = "chars/traveler-1";
        var destLocId = "locations/destination-1";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("TravelLazinessTest"), Name = "TravelLazinessTest" });

            var startLoc = new Location
            {
                Id = "locations/start", Name = "The Start", CampaignName = "TravelLazinessTest",
                Type = LocationType.Settlement
            };
            await session.StoreAsync(startLoc);

            var c = new Character
            {
                Id = charId, Name = "Lazy Bob", CampaignName = "TravelLazinessTest",
                CurrentLocationId = "locations/start"
            };
            await session.StoreAsync(c);

            var loc = new Location
            {
                Id = destLocId, Name = "The Goal", CampaignName = "TravelLazinessTest", Type = LocationType.Settlement
            };
            await session.StoreAsync(loc);

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        // 1. LLM issues ActivityChange indicating travel, but FORGETS TravelChange
        var changes = new WorldChange[]
        {
            new ActivityChange
                { CharacterId = charId, NewActivity = $"Travel interrupted en route to {destLocId} by an ambush!" }
        };
        var commitResult = await tools.Commit(changes, "Started traveling and got ambushed");
        Assert.True(commitResult.Success, commitResult.Summary);

        // 2. Advance time (simulating a full day passing without arriving)
        await tools.AdvanceWorld(1, TimeOfDay.Morning, "TravelLazinessTest");

        // 3. Next scene load should nag the LLM
        var sceneResult = await tools.GetScene("locations/start", true, "TravelLazinessTest");
        Assert.True(sceneResult.Success);
        var view = sceneResult.Data;
        Assert.NotNull(view);

        // Assert the pressure exists
        var pressures = sceneResult.WorldPressure;
        Assert.NotNull(pressures);
        Assert.Contains(pressures, p => p.Contains("interrupted en route"));

        // Assert the suggested example is perfectly formed JSON
        Assert.NotNull(view.SuggestedCommitExamples);
        var suggestedJson =
            view.SuggestedCommitExamples.FirstOrDefault(j => j.Contains("\"travel\"") && j.Contains(charId));
        Assert.NotNull(suggestedJson);

        // 4. LLM applies the suggested JSON verbatim (but replaces the placeholder with the real destination)
        suggestedJson = suggestedJson.Replace("locations/actual-dest", destLocId);
        var correctedChanges = System.Text.Json.JsonSerializer.Deserialize<WorldChange[]>(suggestedJson);
        Assert.NotNull(correctedChanges);
        Assert.Equal(2, correctedChanges.Length);
        Assert.IsType<ActivityChange>(correctedChanges[0]);
        Assert.IsType<TravelChange>(correctedChanges[1]);

        var fixCommitResult = await tools.Commit(correctedChanges, "Arriving after the ambush");
        Assert.True(fixCommitResult.Success, fixCommitResult.Error);

        // 5. Verify the pressure clears
        var finalSceneResult = await tools.GetScene("locations/start", true, "TravelLazinessTest");
        if (finalSceneResult.WorldPressure != null)
        {
            Assert.DoesNotContain(finalSceneResult.WorldPressure, p => p.Contains("interrupted en route"));
        }
    }

    [Fact]
    public async Task LLM_Ignores_Faction_Influence_Shift_Produces_PresencePressure()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("FactionInfluenceTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("FactionInfluenceTest"), Name = "FactionInfluenceTest" });

            var loc = new Location
            {
                Id = "locations/town_01", Name = "Town", CampaignName = "FactionInfluenceTest",
                Type = LocationType.Settlement
            };
            await session.StoreAsync(loc);

            var fac = new Faction
            {
                Id = "factions/guild", Name = "The Guild", CampaignName = "FactionInfluenceTest", TerritoryLocationIds =
                    ["locations/town_01"]
            };
            await session.StoreAsync(fac);

            var ev = new Event
            {
                Id = "events/guild_expand", CampaignName = "FactionInfluenceTest", Category = EventCategory.Simulation,
                Summary = "The Guild grew in influence (+10).", Involved =
                    ["factions/guild"],
                DayLogged = 0
            };
            await session.StoreAsync(ev);

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        // Simulating get_scene right after the event
        var sceneResult = await tools.GetScene("locations/town_01", true, "FactionInfluenceTest");
        Assert.True(sceneResult.Success, string.Join(", ", sceneResult.Summary));

        var pressures = sceneResult.WorldPressure;
        Assert.NotNull(pressures);
        Assert.Contains(pressures, p => p.Contains("expanded influence here"));

        // Simulate LLM using suggested commit
        var changes = new WorldChange[]
        {
            new EventOccurred
            {
                Category = EventCategory.SceneCommit, Summary = "Reflected faction influence", Involved =
                    ["factions/guild"]
            }
        };
        var commitResult = await tools.Commit(changes, "Adding rumor for faction influence", "FactionInfluenceTest");
        Assert.True(commitResult.Success, commitResult.Error);

        using (var s = _store.OpenAsyncSession())
        {
            await s.StoreAsync(new Event { Id = "events/dummy1", CampaignName = "FactionInfluenceTest" });
            s.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true,
                indexes: ["Event/Search"]);
            await s.SaveChangesAsync();
        }

        // Next scene load should clear pressure
        var finalSceneResult = await tools.GetScene("locations/town_01", true, "FactionInfluenceTest");
        if (finalSceneResult.WorldPressure != null)
        {
            Assert.DoesNotContain(finalSceneResult.WorldPressure, p => p.Contains("expanded influence here"));
        }
    }

    [Fact]
    public async Task LLM_Ignores_Faction_War_Produces_ReputationPressure()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("FactionWarTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta("FactionWarTest"), Name = "FactionWarTest" });

            var loc = new Location
            {
                Id = "locations/town_02", Name = "Town 2", CampaignName = "FactionWarTest",
                Type = LocationType.Settlement
            };
            await session.StoreAsync(loc);

            var fac = new Faction
            {
                Id = "factions/kingdom", Name = "The Kingdom", CampaignName = "FactionWarTest", TerritoryLocationIds =
                    ["locations/town_02"]
            };
            await session.StoreAsync(fac);

            var c = new Character
            {
                Id = "chars/local_01", Name = "Local Peasant", CampaignName = "FactionWarTest",
                CurrentLocationId = "locations/town_02"
            };
            await session.StoreAsync(c);

            var ev = new Event
            {
                Id = "events/kingdom_war", CampaignName = "FactionWarTest", Category = EventCategory.Simulation,
                Summary = "The Kingdom became Hostile toward Rebels.", Involved =
                    ["factions/kingdom"],
                DayLogged = 0
            };
            await session.StoreAsync(ev);

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        var sceneResult = await tools.GetScene("locations/town_02", true, "FactionWarTest");
        Assert.True(sceneResult.Success, string.Join(", ", sceneResult.Summary));

        var pressures = sceneResult.WorldPressure;
        Assert.NotNull(pressures);
        Assert.Contains(pressures, p => p.Contains("recent hostilities"));

        var changes = new WorldChange[]
        {
            new FactionReputationChange { CharacterId = "chars/local_01", FactionId = "factions/kingdom", Delta = -20 }
        };
        var commitResult = await tools.Commit(changes, "Updating peasant stance on war", "FactionWarTest");
        Assert.True(commitResult.Success, commitResult.Error);

        using (var s = _store.OpenAsyncSession())
        {
            await s.StoreAsync(new Event { Id = "events/dummy2", CampaignName = "FactionWarTest" });
            s.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5));
            await s.SaveChangesAsync();
        }

        var finalSceneResult = await tools.GetScene("locations/town_02", true, "FactionWarTest");
        if (finalSceneResult.WorldPressure != null &&
            finalSceneResult.WorldPressure.Any(p => p.Contains("recent hostilities")))
        {
            using (var s = _store.OpenAsyncSession())
            {
                var evs = await s.Query<Event>().Where(e => e.CampaignName == "FactionWarTest").ToListAsync();
                var msg = "Events found: " + string.Join("; ",
                    evs.Select(e =>
                        $"{e.Category} {e.DayLogged} {e.Timestamp:O} Inv:[{string.Join(",", e.Involved ?? [])}]"));
                throw new Exception("Pressure still present. " + msg);
            }
        }
    }

    [Fact]
    public async Task LLM_Leaves_Quest_Stale_Produces_DeadlinePressure_And_Resolves()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("QuestStaleTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta("QuestStaleTest"), Name = "QuestStaleTest" });

            var loc = new Location
            {
                Id = "locations/town_03", Name = "Town 3", CampaignName = "QuestStaleTest",
                Type = LocationType.Settlement
            };
            await session.StoreAsync(loc);

            var q = new Quest
            {
                Id = "quests/q1",
                Title = "The Stale Quest",
                CampaignName = "QuestStaleTest",
                OverallState = QuestState.InProgress,
                DeadlineDay = 15,
                RelatedLocationIds = ["locations/town_03"],
                Objectives = [new QuestObjective("Do something", QuestState.InProgress)]
            };
            await session.StoreAsync(q);

            session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5));
            await session.SaveChangesAsync();
        }

        var advanceResult = await tools.AdvanceWorld(14, TimeOfDay.Dawn, "QuestStaleTest");
        Assert.True(advanceResult.Success, advanceResult.Error);

        var sceneResult = await tools.GetScene("locations/town_03", true, "QuestStaleTest");
        Assert.True(sceneResult.Success, sceneResult.Error);

        Assert.NotNull(sceneResult.WorldPressure);
        Assert.Contains(sceneResult.WorldPressure, p => p.Contains("Quest 'The Stale Quest' deadline"));
        Assert.NotNull(sceneResult.Data!.SuggestedCommitExamples);
        Assert.Contains(sceneResult.Data!.SuggestedCommitExamples, p => p.Contains("quest_progress"));

        var changes = new WorldChange[]
        {
            new QuestProgress
            {
                QuestId = "quests/q1", ObjectiveIndex = 0, NewState = QuestState.Failed,
                NarrativeNote = "Failed to complete the objective in time."
            }
        };
        var commitResult = await tools.Commit(changes, "Failing quest due to time limit", "QuestStaleTest");
        Assert.True(commitResult.Success, commitResult.Error);

        // Wait for RavenDB indexes to catch up so the quest search returns accurate state
        var finalSceneResult = await tools.GetScene("locations/town_03", true, "QuestStaleTest");
        for (int i = 0;
             i < 15 && finalSceneResult.WorldPressure != null &&
             finalSceneResult.WorldPressure.Any(p => p.Contains("Quest 'The Stale Quest' deadline"));
             i++)
        {
            await Task.Delay(200);
            finalSceneResult = await tools.GetScene("locations/town_03", true, "QuestStaleTest");
        }

        if (finalSceneResult.WorldPressure != null)
        {
            Assert.DoesNotContain(finalSceneResult.WorldPressure, p => p.Contains("Quest 'The Stale Quest' deadline"));
        }
    }

    [Fact]
    public async Task Active_Quest_Giver_Is_Protected_From_Eviction()
    {
        var evictionRule =
            new TransientEvictionRule(Microsoft.Extensions.Logging.Abstractions.NullLogger<TransientEvictionRule>
                .Instance);
        var simEngine = new DefaultSimulationEngine([evictionRule],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultSimulationEngine>.Instance);
        var repo = _fixture.CreateRepository(engineOverride: simEngine);
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo, simulationEngine: simEngine);

        await tools.SelectCampaign("QuestGiverEvictionTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("QuestGiverEvictionTest"), Name = "QuestGiverEvictionTest" });

            var loc = new Location
            {
                Id = "locations/town_04", Name = "Town 4", CampaignName = "QuestGiverEvictionTest",
                Type = LocationType.Settlement
            };
            // Simulate that it was visited a long time ago, so transient rule kicks in if not protected
            loc.LastVisitedDay = 0;
            await session.StoreAsync(loc);

            var c = new Character
            {
                Id = "chars/transient_giver", Name = "Transient Guy", CampaignName = "QuestGiverEvictionTest",
                CurrentLocationId = "locations/town_04", KeepAlive = false
            };
            await session.StoreAsync(c);

            var q = new Quest
            {
                Id = "quests/q2",
                Title = "Giver Protection Quest",
                CampaignName = "QuestGiverEvictionTest",
                OverallState = QuestState.InProgress,
                GiverId = "chars/transient_giver",
                RelatedLocationIds = ["locations/town_04"],
                Objectives = [new QuestObjective("Do something", QuestState.InProgress)]
            };
            await session.StoreAsync(q);

            session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5));
            await session.SaveChangesAsync();

            // Force index creation so AdvanceWorld doesn't get 0 results on first run
            await session.Query<Quest>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                .ToListAsync();

            await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(c => c.CurrentLocationId != null)
                .ToListAsync();
        }

        // Advance 3 days, eviction should skip the quest giver
        var advanceResult1 = await tools.AdvanceWorld(3, TimeOfDay.Dawn, "QuestGiverEvictionTest");
        Assert.True(advanceResult1.Success, advanceResult1.Error);

        using (var session = _store.OpenAsyncSession())
        {
            var c = await session.LoadAsync<Character>("chars/transient_giver");
            Assert.NotNull(c.CurrentLocationId); // He is NOT evicted!
        }

        // Now complete the quest
        var changes = new WorldChange[]
        {
            new QuestProgress
                { QuestId = "quests/q2", ObjectiveIndex = 0, NewState = QuestState.Complete, NarrativeNote = "Done." }
        };
        var commitResult = await tools.Commit(changes, "Finishing quest", "QuestGiverEvictionTest");
        Assert.True(commitResult.Success, commitResult.Error);

        using (var s = _store.OpenAsyncSession())
        {
            // Wait for index to reflect the completed quest
            await s.Query<Quest>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(q => q.OverallState == QuestState.Open || q.OverallState == QuestState.InProgress)
                .ToListAsync();

            var quest = await s.LoadAsync<Quest>("quests/q2");
            Assert.Equal(QuestState.Complete, quest.OverallState);

            // Also ensure the character index used by TransientEvictionRule is not stale
            await s.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(c => c.CurrentLocationId != null)
                .ToListAsync();
        }

        // Advance another 3 days, now he should be evicted
        var advanceResult2 = await tools.AdvanceWorld(3, TimeOfDay.Dawn, "QuestGiverEvictionTest");
        Assert.True(advanceResult2.Success, advanceResult2.Error);

        using (var session = _store.OpenAsyncSession())
        {
            var c = await session.LoadAsync<Character>("chars/transient_giver");
            Assert.Null(c.CurrentLocationId); // He IS evicted!
        }
    }

    [Fact]
    public async Task GetScene_PartyPresentWithoutTravel_ProducesMissingTravelCommit_And_Resolves_On_Commit()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("MissingTravelTest");

        var pcId = "chars/pc1";
        var destId = "locations/dest-missing-travel";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta("MissingTravelTest"), Name = "MissingTravelTest" });
            await session.StoreAsync(new Location
            {
                Id = "locations/start-mt", Name = "Start", CampaignName = "MissingTravelTest",
                Type = LocationType.Settlement
            });
            await session.StoreAsync(new Location
                { Id = destId, Name = "Far Town", CampaignName = "MissingTravelTest", Type = LocationType.Settlement });
            await session.StoreAsync(new Character
            {
                Id = pcId,
                Name = "Hero",
                CampaignName = "MissingTravelTest",
                KeepAlive = true,
                CurrentLocationId = "locations/start-mt"
            });
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: false,
                indexes: ["Location/Search", "Character/Search"]);
            await session.SaveChangesAsync();
        }

        // LLM narrates arrival but forgets travel commit — party still at start
        var sceneResult = await tools.GetScene(destId, partyPresent: true, "MissingTravelTest");
        Assert.True(sceneResult.Success);
        Assert.NotNull(sceneResult.WorldPressure);
        Assert.Contains(sceneResult.WorldPressure,
            p => p.Contains("NO main characters") || p.Contains("forget to commit their travel"));

        var fix = await tools.Commit([
            new TravelChange
            {
                CharacterId = pcId, DestinationLocationId = destId, Narrative = "Arrived at Far Town",
                EncounterRiskModifier = -100
            }
        ], "Party travels to Far Town", "MissingTravelTest");
        Assert.True(fix.Success, fix.Error);

        using (var s = _store.OpenAsyncSession())
        {
            await s.Advanced.AsyncDocumentQuery<Character, Character_Search>()
                .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
                .Take(1)
                .AnyAsync();
        }

        var finalScene = await tools.GetScene(destId, partyPresent: true, "MissingTravelTest");
        if (finalScene.WorldPressure != null)
        {
            Assert.DoesNotContain(finalScene.WorldPressure, p => p.Contains("forget to commit their travel"));
        }
    }

    [Fact]
    public async Task GetScene_TransientQuestGiver_ProducesKeepAlivePressure_And_Resolves_On_CharacterUpdate()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("TransientGiverPressureTest");

        var giverId = "chars/quest_giver_pressure";
        var locId = "locations/giver-town";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("TransientGiverPressureTest"), Name = "TransientGiverPressureTest" });
            await session.StoreAsync(new Location
            {
                Id = locId, Name = "Giver Town", CampaignName = "TransientGiverPressureTest",
                Type = LocationType.Settlement, LastVisitedDay = 1
            });
            await session.StoreAsync(new Character
            {
                Id = giverId, Name = "Bram", CampaignName = "TransientGiverPressureTest", KeepAlive = false,
                CurrentLocationId = locId, CurrentActivity = "Waiting for the party"
            });
            await session.StoreAsync(new Quest
            {
                Id = "quests/giver_q",
                Title = "Bram's Errand",
                CampaignName = "TransientGiverPressureTest",
                GiverId = giverId,
                OverallState = QuestState.InProgress,
                RelatedLocationIds = [locId],
                Objectives = [new QuestObjective("Deliver the package", QuestState.InProgress)]
            });
            session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        var sceneResult = await tools.GetScene(locId, true, "TransientGiverPressureTest");
        Assert.True(sceneResult.Success);
        Assert.NotNull(sceneResult.WorldPressure);
        Assert.Contains(sceneResult.WorldPressure, p => p.Contains("Quest Giver") && p.Contains("character_update"));

        var fix = await tools.Commit([new CharacterUpdate { CharacterId = giverId, KeepAlive = true }],
            "Anchor quest giver", "TransientGiverPressureTest");
        Assert.True(fix.Success, fix.Error);

        var finalScene = await tools.GetScene(locId, true, "TransientGiverPressureTest");
        if (finalScene.WorldPressure != null)
        {
            Assert.DoesNotContain(finalScene.WorldPressure, p => p.Contains("Quest Giver"));
        }
    }

    [Fact]
    public async Task QuestProgress_ClearsStaleQuestPressureCooldown()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("QuestCooldownTest");
        var questId = "quests/cooldown_q";
        var locId = "locations/cooldown-town";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta("QuestCooldownTest"), Name = "QuestCooldownTest" });
            await session.StoreAsync(new CampaignTime
                { Id = keys.StateTime("QuestCooldownTest"), TotalDaysElapsed = 20 });
            await session.StoreAsync(new Location
                { Id = locId, Name = "Town", CampaignName = "QuestCooldownTest", Type = LocationType.Settlement });
            await session.StoreAsync(new Quest
            {
                Id = questId,
                Title = "Cooldown Quest",
                CampaignName = "QuestCooldownTest",
                OverallState = QuestState.Open,
                LastUpdatedDay = 5,
                RelatedLocationIds = [locId],
                Objectives = [new QuestObjective("Step one", QuestState.Open)]
            });
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true,
                indexes: ["Location/Search", "Quest/Search"]);
            await session.SaveChangesAsync();
        }

        var first = await tools.GetScene(locId, true, "QuestCooldownTest");
        Assert.NotNull(first.WorldPressure);
        Assert.Contains(first.WorldPressure, p => p.Contains("Cooldown Quest"));

        // Same day — suppressed by cooldown
        var second = await tools.GetScene(locId, true, "QuestCooldownTest");
        Assert.Null(second.WorldPressure);

        // Fix via quest_progress — should clear cooldown even though quest still open
        var fix = await tools.Commit(
            [new QuestProgress { QuestId = questId, ObjectiveIndex = 0, NewState = QuestState.InProgress }],
            "Made progress", "QuestCooldownTest");
        Assert.True(fix.Success, fix.Error);

        var third = await tools.GetScene(locId, true, "QuestCooldownTest");
        // Progress updated LastUpdatedDay — stale pressure should not re-fire immediately
        if (third.WorldPressure != null)
        {
            Assert.DoesNotContain(third.WorldPressure, p => p.Contains("seen no progress in over 10 days"));
        }
    }

    [Fact]
    public async Task GetScene_QuestStaleness_UsesOldestOpenObjective_NotLastQuestTouch()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("ObjectiveStaleTest");
        var locId = "locations/obj-stale-town";

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("ObjectiveStaleTest"), Name = "ObjectiveStaleTest" });
            await session.StoreAsync(new CampaignTime
                { Id = keys.StateTime("ObjectiveStaleTest"), TotalDaysElapsed = 20 });
            await session.StoreAsync(new Location
                { Id = locId, Name = "Town", CampaignName = "ObjectiveStaleTest", Type = LocationType.Settlement });
            await session.StoreAsync(new Quest
            {
                Id = "quests/multi_obj",
                Title = "Two Steps",
                CampaignName = "ObjectiveStaleTest",
                OverallState = QuestState.InProgress,
                LastUpdatedDay = 18, // recent quest touch
                RelatedLocationIds = [locId],
                Objectives =
                [
                    new QuestObjective("First step", QuestState.Complete, DayStarted: 5, DayCompleted: 6),
                    new QuestObjective("Second step", QuestState.Open, DayStarted: 5) // stale 15 days
                ]
            });
            session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        var sceneResult = await tools.GetScene(locId, true, "ObjectiveStaleTest");
        Assert.NotNull(sceneResult.WorldPressure);
        Assert.Contains(sceneResult.WorldPressure,
            p => p.Contains("Two Steps") && p.Contains("no progress in over 10 days"));
    }

    [Fact]
    public async Task GetScene_QuestStaleness_ProducesNarrativePrompt_And_Resolves_On_Commit()
    {
        var repo = _fixture.CreateRepository();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("QuestStalenessTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign
                { Id = keys.Meta("QuestStalenessTest"), Name = "QuestStalenessTest" });

            var time = new CampaignTime { Id = keys.StateTime("QuestStalenessTest"), TotalDaysElapsed = 20 };
            await session.StoreAsync(time);

            var loc = new Location
            {
                Id = "locations/town_01", Name = "Town", CampaignName = "QuestStalenessTest",
                Type = LocationType.Settlement
            };
            await session.StoreAsync(loc);

            var quest = new Quest
            {
                Id = "quests/stale_quest",
                Title = "Stale Quest",
                CampaignName = "QuestStalenessTest",
                OverallState = QuestState.Open,
                LastUpdatedDay = 5, // 15 days ago
                RelatedLocationIds = ["locations/town_01"],
                Objectives = [new QuestObjective("Find the thing")]
            };
            await session.StoreAsync(quest);

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        var sceneResult = await tools.GetScene("locations/town_01", true, "QuestStalenessTest");
        Assert.True(sceneResult.Success, sceneResult.Error);

        var pressures = sceneResult.WorldPressure;
        Assert.NotNull(pressures);
        Assert.Contains(pressures, p => p.Contains("Quest 'Stale Quest' has seen no progress in over 10 days"));

        var changes = new WorldChange[]
        {
            new QuestProgress { QuestId = "quests/stale_quest", ObjectiveIndex = 0, NewState = QuestState.InProgress }
        };
        var commitResult = await tools.Commit(changes, "Made progress on stale quest");
        Assert.True(commitResult.Success, commitResult.Error);

        var finalSceneResult = await tools.GetScene("locations/town_01", true, "QuestStalenessTest");
        if (finalSceneResult.WorldPressure != null)
        {
            Assert.DoesNotContain(finalSceneResult.WorldPressure, p => p.Contains("Stale Quest"));
        }
    }

    [Fact]
    public async Task GetHelp_ContainsPhase7Examples()
    {
        var repo = _fixture.CreateRepository();
        var rollSvc = new DefaultRollService();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        var result = await tools.GetHelp();
        Assert.True(result.Success);
        Assert.Contains("Travel, Faction, Quest & Rumor", result.Data);
        Assert.Contains("faction_reputation", result.Data);
        Assert.Contains("quest_progress", result.Data);
        Assert.Contains("destinationLocationId", result.Data);
    }

    [Fact]
    public async Task GetHelp_ContainsPhase8SandboxExamples()
    {
        var repo = _fixture.CreateRepository();
        var rollSvc = new DefaultRollService();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        var result = await tools.GetHelp();
        Assert.True(result.Success);
        Assert.Contains("The Visual / Physics Sandbox", result.Data);
        Assert.Contains("item_update", result.Data);
        Assert.Contains("character_update", result.Data);
    }

    [Fact]
    public async Task LLM_UsesItemUpdate_And_CharacterUpdate_For_VisualState()
    {
        var repo = _fixture.CreateRepository();
        var rollSvc = new DefaultRollService();
        var keys = new CampaignDocumentKeys();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);

        await tools.SelectCampaign("VisualStateTest");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = keys.Meta("VisualStateTest"), Name = "VisualStateTest" });
            var time = new CampaignTime { Id = keys.StateTime("VisualStateTest"), TotalDaysElapsed = 1 };
            await session.StoreAsync(time);

            var loc = new Location
            {
                Id = "locations/tavern_01", Name = "Tavern", CampaignName = "VisualStateTest",
                Type = LocationType.Settlement
            };
            await session.StoreAsync(loc);

            var c = new Character
            {
                Id = "chars/bob", Name = "Bob", CampaignName = "VisualStateTest", KeepAlive = true,
                CurrentLocationId = "locations/tavern_01"
            };
            await session.StoreAsync(c);

            var item = new Item
            {
                Id = "items/sword", Name = "Sword", CampaignName = "VisualStateTest", HolderId = "locations/tavern_01",
                CoreCategory = ItemCategory.Weapon
            };
            await session.StoreAsync(item);

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5), throwOnTimeout: true);
            await session.SaveChangesAsync();
        }

        var changes = new WorldChange[]
        {
            new CharacterUpdate
            {
                CharacterId = "chars/bob", AppearanceOverride = "Covered in mud", TagsToAdd = ["muddy"],
                FeaturesToAdd = ["Scar"]
            },
            new ItemUpdate
                { ItemId = "items/sword", NewState = "Dull", TagsToAdd = ["rusty"], FeaturesToAdd = ["Leather wrap"] }
        };
        var commitResult = await tools.Commit(changes, "Bob fell in mud");
        Assert.True(commitResult.Success, commitResult.Error);

        var sceneResult = await tools.GetScene("locations/tavern_01", true, "VisualStateTest");
        Assert.True(sceneResult.Success, sceneResult.Error);

        var view = sceneResult.Data;
        Assert.NotNull(view);

        var bob = view.PresentNPCs.First(n => n.Id == "chars/bob");
        Assert.Equal("Covered in mud", bob.CurrentAppearance);
        Assert.NotNull(bob.VisualTags);
        Assert.Contains("muddy", bob.VisualTags);
        Assert.NotNull(bob.DistinctiveFeatures);
        Assert.Contains("Scar", bob.DistinctiveFeatures);

        var sword = view.VisibleItems.First(i => i.Id == "items/sword");
        Assert.Equal("Dull", sword.CurrentState);
        Assert.NotNull(sword.Tags);
        Assert.Contains("rusty", sword.Tags);
        Assert.NotNull(sword.DistinctiveFeatures);
        Assert.Contains("Leather wrap", sword.DistinctiveFeatures);
    }
}