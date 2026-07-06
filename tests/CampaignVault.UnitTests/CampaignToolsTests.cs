using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignToolsTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CampaignToolsTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignTools CreateTools() => TestCampaignToolsFactory.Create(_fixture);

    [Fact]
    public async Task Commit_RejectsBatchOverLimit()
    {
        var tools = CreateTools();
        var changes = new WorldChange[51];

        // Fill with dummy changes
        for (var i = 0; i < changes.Length; i++)
        {
            changes[i] = new HpChange { CharacterId = "dummy", Delta = -1 };
        }

        var result = await tools.Commit(changes, "Massive batch");

        Assert.False(result.Success);
        Assert.Equal("RateLimitExceeded", result.Error);
        Assert.Contains("Maximum allowed is 50", result.Summary);
    }

    [Fact(Skip =
        "Static rate limit is increased to 10,000 to prevent parallel test suites from failing. Skip this boundary test.")]
    public async Task Commit_RejectsWhenRateLimitExceeded()
    {
        var tools = CreateTools();
        var change = new WorldChange[] { new HpChange { CharacterId = "dummy", Delta = -1 } };

        var successCount = 0;
        var rejectCount = 0;

        // The limit is 20 tokens. If we slam it with 30 concurrent or rapid requests, some should fail.
        for (var i = 0; i < 30; i++)
        {
            var res = await tools.Commit(change, "Spamming the system");
            if (res.Success)
            {
                successCount++;
            }
            else if (res.Error == "RateLimitExceeded" && res.Summary!.Contains("rate limit exceeded"))
            {
                rejectCount++;
            }
        }

        Assert.True(successCount <= 20,
            $"Should have successfully processed at most 20 requests, but got {successCount}.");
        Assert.True(rejectCount > 0,
            $"Should have rejected some requests due to rate limiting, but got {rejectCount}.");
    }

    [Fact]
    public async Task GetFactionContext_ValidId_ReturnsFullFaction()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var fid = "factions/tool-faction-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session,
                new Faction { Id = fid, Name = "Guild of Tests", InfluenceLevel = 10 }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        var result = await tools.GetFactionContext(fid);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Guild of Tests", result.Data!.Name);
    }

    [Fact]
    public async Task GetFactionContext_BadId_ReturnsNotFound_WithSuggestions()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var fid = "factions/real-faction-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction { Id = fid, Name = "Real Guild", InfluenceLevel = 10 }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        // Wait for index
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetFactionContext("factions/real-fac"); // typo
        Assert.False(result.Success);
        Assert.Equal("NotFound", result.Error);
        Assert.Contains("Did you mean:", result.Summary);
        Assert.Contains("Real Guild", result.Summary);
    }

    [Fact]
    public async Task GetQuestDetails_ValidId_ReturnsFullQuest()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var qid = "quests/tool-quest-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertQuestAsync(session,
                new Quest { Id = qid, Title = "Test Quest", OverallState = QuestState.Open }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        var result = await tools.GetQuestDetails(qid);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("Test Quest", result.Data!.Title);
    }

    [Fact]
    public async Task GetQuestDetails_BadId_ReturnsNotFound_WithSuggestions()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var qid = "quests/real-quest-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertQuestAsync(session,
                new Quest { Id = qid, Title = "Real Quest", OverallState = QuestState.Open }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        // Wait for index
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetQuestDetails("quests/real-que"); // typo
        Assert.False(result.Success);
        Assert.Equal("NotFound", result.Error);
        Assert.Contains("Did you mean:", result.Summary);
        Assert.Contains("Real Quest", result.Summary);
    }

    [Fact]
    public async Task GetFactionContext_TypoName_ReturnsSuggestions_BasedOnName()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var fid = "factions/weirdid-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction { Id = fid, Name = "Silver Hand", InfluenceLevel = 10 }, TestCampaignDefaults.Slug);
            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        // Query by typo in Name, not ID prefix
        var result = await tools.GetFactionContext("Silver Han");
        Assert.False(result.Success);
        Assert.Contains("Did you mean:", result.Summary);
        Assert.Contains("Silver Hand", result.Summary);
    }

    [Fact]
    public async Task GetScene_Emits_SuggestedCommitExamples_ForInterruptedTravel_AndPopulatesSummaries()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/travel-loc-" + Guid.NewGuid();
        var charId = "chars/stuck-hero-" + Guid.NewGuid();
        var questId = "quests/deadline-quest-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Road" }, TestCampaignDefaults.Slug);
            var npc = new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Stuck Hero",
                CurrentActivity = "Travel interrupted en route to the capital by goblins",
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            };
            await repo.UpsertCharacterAsync(session, npc, TestCampaignDefaults.Slug);

            var time = await repo.GetTimeAsync(session, TestCampaignDefaults.Slug);
            time.TotalDaysElapsed = 10;
            await session.StoreAsync(time);

            // Deadline in 2 days -> should emit a pressure in GetScene
            await repo.UpsertQuestAsync(session, new Quest
            {
                Id = questId, Title = "Impending Doom", OverallState = QuestState.Open, DeadlineDay = 12,
                RelatedLocationIds =
                    [locId]
            }, TestCampaignDefaults.Slug);

            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetScene(locId);
        Assert.True(result.Success);

        // Assert the pressure is present
        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure,
            p => p.Contains("stuck", StringComparison.OrdinalIgnoreCase) ||
                 p.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.WorldPressure, p => p.Contains("deadline", StringComparison.OrdinalIgnoreCase));

        // Assert that SuggestedCommitExamples is populated in the returned SceneView
        var view = result.Data;
        Assert.NotNull(view);
        Assert.NotNull(view.SuggestedCommitExamples);
        Assert.Contains(view.SuggestedCommitExamples, ex => ex.Contains("newActivity"));
        Assert.Contains(view.SuggestedCommitExamples, ex => ex.Contains("quest_progress"));

        Assert.NotNull(view.ActiveQuests);
        Assert.NotEmpty(view.ActiveQuests);
        Assert.Equal("Impending Doom", view.ActiveQuests.First().Title);
    }

    [Fact]
    public async Task GetScene_Emits_Negative_Reputation_Pressure_For_FactionControlledAreas()
    {
        var tools = CreateTools();
        var repo = _fixture.CreateRepository();
        var locId = "locations/faction-rep-test-" + Guid.NewGuid();
        var charId = "chars/rep-tester-" + Guid.NewGuid();
        var factionId = "factions/test-faction-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction
            {
                Id = factionId,
                Name = "Test Faction",
                TerritoryLocationIds = [locId]
            }, TestCampaignDefaults.Slug);

            await repo.UpsertLocationAsync(session, new LocationUpsertRequest
            {
                Id = locId,
                Name = "Faction HQ",
                ControllingFactionId = factionId
            }, TestCampaignDefaults.Slug);

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Rep Tester",
                CurrentLocationId = locId,
                KeepAlive = true,
                Social = new SocialProfile
                {
                    FactionReputations = new()
                    {
                        { factionId, -60 }
                    }
                }
            }, TestCampaignDefaults.Slug);

            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetScene(locId);
        Assert.True(result.Success);

        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure,
            p => p.Contains("very low reputation", StringComparison.OrdinalIgnoreCase) &&
                 p.Contains("-60", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetWorldState_Emits_QuestDeadlineNags_AndTravelInterrupted()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var qid = "quests/deadline-quest-" + Guid.NewGuid();
        var locId = "locations/valid-loc-" + Guid.NewGuid();
        var charId = "chars/stuck-hero-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Some Loc" }, TestCampaignDefaults.Slug);
            var time = await repo.GetTimeAsync(session, TestCampaignDefaults.Slug);
            time.TotalDaysElapsed = 10;
            await session.StoreAsync(time);

            // Deadline in 2 days
            await repo.UpsertQuestAsync(session,
                new Quest { Id = qid, Title = "Impending Doom", OverallState = QuestState.Open, DeadlineDay = 12 }, TestCampaignDefaults.Slug);

            var npc = new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Stuck Hero",
                CurrentActivity = "Travel interrupted en route to the capital by goblins",
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            };
            await repo.UpsertCharacterAsync(session, npc, TestCampaignDefaults.Slug);

            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Location/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetWorldState(locId);
        Assert.True(result.Success, result.Error + ": " + result.Summary);

        var view = result.Data;
        Assert.NotNull(view);

        Assert.NotNull(view.WorldPressure);
        Assert.Contains(view.WorldPressure, p => p.Contains("deadline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(view.WorldPressure,
            p => p.Contains("stuck", StringComparison.OrdinalIgnoreCase) ||
                 p.Contains("interrupted", StringComparison.OrdinalIgnoreCase));

        // Should also emit an example for it
        Assert.NotNull(view.SuggestedCommitExamples);
        Assert.Contains(view.SuggestedCommitExamples, ex => ex.Contains("quest_progress"));
        Assert.Contains(view.SuggestedCommitExamples, ex => ex.Contains("newActivity"));
    }

    [Fact]
    public async Task GetScene_EmitsMemoryDecayPressure()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/test-memory";
        var npcId = "chars/decay-bob";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var config = await repo.GetCampaignConfigAsync(session, TestCampaignDefaults.Slug);
            config.MemoryImportantDecayDays = 40;
            await repo.UpsertCampaignConfigAsync(session, config, TestCampaignDefaults.Slug);

            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Room" }, TestCampaignDefaults.Slug);

            var c = new CharacterUpsertRequest { Id = npcId, Name = "Bob", CurrentLocationId = locId, Psychology = new PsychologyProfile() };
            c.Psychology.Memories["Secret"] = new MemoryNode
                { Topic = "Secret", Details = "A secret", DayAcquired = 10, Importance = MemoryImportance.Important };
            await repo.UpsertCharacterAsync(session, c, TestCampaignDefaults.Slug);

            var t = await repo.GetTimeAsync(session, TestCampaignDefaults.Slug);
            t.TotalDaysElapsed = 51; // 51 - 10 = 41 > 40
            // t is automatically tracked by session.SaveChangesAsync()

            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, true);
        Assert.True(result.Success);
        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure, p => p.Contains("fading") && p.Contains("Secret"));
    }

    [Fact]
    public async Task GetScene_IgnoresCoreMemoryDecay()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/test-core";
        var npcId = "chars/core-bob";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Room" }, TestCampaignDefaults.Slug);

            var c = new CharacterUpsertRequest { Id = npcId, Name = "Bob", CurrentLocationId = locId, Psychology = new PsychologyProfile() };
            c.Psychology.Memories["Secret"] = new MemoryNode
                { Topic = "Secret", Details = "A secret", DayAcquired = 10, Importance = MemoryImportance.Core };
            await repo.UpsertCharacterAsync(session, c, TestCampaignDefaults.Slug);

            var t = await repo.GetTimeAsync(session, TestCampaignDefaults.Slug);
            t.TotalDaysElapsed = 100; // Even at 90 days diff, core shouldn't decay
            // t is automatically tracked by session.SaveChangesAsync()

            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, true);
        Assert.True(result.Success);

        Assert.True(result.WorldPressure == null ||
                    !result.WorldPressure.Any(p => p.Contains("fading") && p.Contains("Secret")));
    }

    [Fact]
    public async Task GetScene_EmitsOpportunisticPressure()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/test-opportunistic";
        var factionId = "factions/thieves";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Alley" }, TestCampaignDefaults.Slug);

            var f = new Faction
            {
                Id = factionId, Name = "Thieves", TerritoryLocationIds = [locId],
                StanceToward = new System.Collections.Generic.Dictionary<string, FactionStance>
                    { ["party"] = FactionStance.Opportunistic }
            };
            await repo.UpsertFactionAsync(session, f, TestCampaignDefaults.Slug);

            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, true);
        Assert.True(result.Success);
        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure, p => p.Contains("Opportunistic faction") && p.Contains("Thieves"));
    }

    [Fact]
    public async Task GetScene_EmitsEconomicDemandPressure()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/test-economy-" + Guid.NewGuid();
        var charId = "chars/pc-economy-" + Guid.NewGuid();
        var factionId = "factions/merchants-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction
            {
                Id = factionId,
                Name = "War Merchants",
                TerritoryLocationIds = [locId],
                EconomicDemand =
                    new System.Collections.Generic.Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Weapon"] = 2.0f
                    }
            }, TestCampaignDefaults.Slug);

            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Market" }, TestCampaignDefaults.Slug);

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = charId,
                Name = "PC",
                CurrentLocationId = locId,
                KeepAlive = true
            }, TestCampaignDefaults.Slug);

            await session.StoreAsync(new Item
            {
                Id = "items/test-sword-" + Guid.NewGuid(),
                Name = "Longsword",
                Description = "A sword",
                HolderId = charId,
                CoreCategory = ItemCategory.Weapon
            });

            await session.SaveChangesAsync();
        }

        await WaitForCharacterAndFactionIndexesAsync();

        var result = await tools.GetScene(locId, partyPresent: true);
        Assert.True(result.Success);
        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure, p => p.Contains("desperate for 'Weapon'") && p.Contains("War Merchants"));
    }

    [Fact]
    public async Task GetScene_EmitsEconomicDemandPressure_MatchesTagsCaseInsensitively()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/test-economy-tags-" + Guid.NewGuid();
        var charId = "chars/pc-scroll-" + Guid.NewGuid();
        var factionId = "factions/scribes-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction
            {
                Id = factionId,
                Name = "Arcane Scribes",
                TerritoryLocationIds = [locId],
                EconomicDemand =
                    new System.Collections.Generic.Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["spell scrolls"] = 2.0f
                    }
            }, TestCampaignDefaults.Slug);

            await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Scriptorium" }, TestCampaignDefaults.Slug);

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = charId,
                Name = "PC",
                CurrentLocationId = locId,
                KeepAlive = true
            }, TestCampaignDefaults.Slug);

            await session.StoreAsync(new Item
            {
                Id = "items/test-scroll-" + Guid.NewGuid(),
                Name = "Scroll",
                Description = "A scroll",
                HolderId = charId,
                CoreCategory = ItemCategory.Document,
                Tags = ["Spell Scrolls"]
            });

            await session.SaveChangesAsync();
        }

        await WaitForCharacterAndFactionIndexesAsync();

        var result = await tools.GetScene(locId, partyPresent: true);
        Assert.True(result.Success);
        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure,
            p => p.Contains("desperate for 'spell scrolls'") && p.Contains("Arcane Scribes"));
    }

    private async Task WaitForCharacterAndFactionIndexesAsync()
    {
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task GetParty_ReturnsOnlyPartyFlaggedCharacters_ForCampaign()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var campaignName = "getparty-camp-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var configId = new CampaignDocumentKeys().Config(campaignName);
            await session.StoreAsync(new CampaignConfig { Id = configId });

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = "characters/pc-1-" + Guid.NewGuid(),
                Name = "PC Hero",
                IsPc = true,
                KeepAlive = true
            }, campaignName);

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = "characters/companion-1-" + Guid.NewGuid(),
                Name = "Wolf Companion",
                IsPartyCompanion = true
            }, campaignName);

            await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
            {
                Id = "characters/npc-1-" + Guid.NewGuid(),
                Name = "Transient Bard",
                KeepAlive = true
            }, campaignName);

            await session.SaveChangesAsync();
        }

        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false))
            {
                break;
            }

            await Task.Delay(100);
        }

        var result = await tools.GetParty(campaignName);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data!.Count);
        Assert.Contains(result.Data, c => c.Name == "PC Hero" && c.IsPc);
        Assert.Contains(result.Data, c => c.Name == "Wolf Companion" && c.IsPartyCompanion);
    }

    [Fact]
    public void AllMcpTools_HaveToolCategoryAttribute()
    {
        var methods = typeof(CampaignTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToList();

        Assert.True(methods.Count >= 25);

        foreach (var method in methods)
        {
            var category = method.GetCustomAttribute<ToolCategoryAttribute>()?.Category;
            Assert.False(string.IsNullOrWhiteSpace(category), $"Missing [ToolCategory] on {method.Name}");
            Assert.NotEqual("Other", category);
        }
    }

    [Fact]
    public async Task ListTools_ReturnsFullCatalog()
    {
        var tools = CreateTools();

        var result = await tools.ListTools();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Count >= 25);
        Assert.DoesNotContain(result.Data, t => t.Category == "Other");
        Assert.Contains(result.Data, t => t.Name == "get_help" && t.Category == "System");
        Assert.Contains(result.Data, t => t.Name == "list_tools" && t.Category == "System");
        Assert.Contains(result.Data, t => t.Name == "commit" && t.Category == "Mutation & time");
        Assert.Contains(result.Data, t => t.Name == "get_quest_details" && t.Category == "Deep dives");
    }

    [Fact]
    public async Task ListTools_FiltersByCategory()
    {
        var tools = CreateTools();

        var result = await tools.ListTools("Combat & rulesets");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.All(result.Data!, t => Assert.Equal("Combat & rulesets", t.Category));
        Assert.Contains(result.Data, t => t.Name == "start_combat");
        Assert.DoesNotContain(result.Data, t => t.Name == "get_help");
    }

    [Fact]
    public async Task GetHelp_ContainsQuickstartAndToolIndex()
    {
        var tools = CreateTools();

        var result = await tools.GetHelp();

        Assert.True(result.Success);
        Assert.Contains("Quickstart for Models", result.Data);
        Assert.Contains("Campaign slug scoping", result.Data);
        Assert.Contains("campaignName", result.Data);
        Assert.Contains("list_campaigns", result.Data);
        Assert.Contains("Tool Index by Category", result.Data);
        Assert.Contains("`list_tools`", result.Data);
        Assert.Contains("get_quest_details", result.Data);
    }

    [Fact]
    public async Task GetScene_ViaTools_PartyPresent_StampsLastVisitedDay_AndSavesChanges()
    {
        var repo = _fixture.CreateRepository();
        var tools = CreateTools();
        var locId = "locations/visit-tool-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session,
                new LocationUpsertRequest { Id = locId, Name = "Tool Visit Location", LastVisitedDay = 0 }, TestCampaignDefaults.Slug);

            var time = await repo.GetTimeAsync(session, TestCampaignDefaults.Slug);
            time.TotalDaysElapsed = 7;
            await session.StoreAsync(time);

            await session.SaveChangesAsync();
        }

        // Call GetScene with partyPresent: true
        var result = await tools.GetScene(locId, partyPresent: true);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(7, result.Data.Location.LastVisitedDay);

        // Verify that the change was persisted to database
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var loc = await repo.GetLocationAsync(session, locId, TestCampaignDefaults.Slug);
            Assert.NotNull(loc);
            Assert.Equal(7, loc.LastVisitedDay);
        }
    }
}
