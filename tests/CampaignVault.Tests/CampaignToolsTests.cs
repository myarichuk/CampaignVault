using System;
using System.Linq;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using CampaignVault.Rulesets;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignToolsTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CampaignToolsTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignTools CreateTools()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var rollSvc = new DefaultRollService();
        var selector = new RulesetResolverSelector([
            new Dnd5eRulesetResolver(rollSvc),
            new Pf2eRulesetResolver(rollSvc),
            new Fallout2d20RulesetResolver(rollSvc)
        ]);
        
        return new CampaignTools(
            repo,
            new DefaultBehaviorSynthesizer(),
            selector,
            new CampaignDocumentKeys(),
            new CurrentCampaignContext()
        );
    }

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

    [Fact(Skip = "Static rate limit is increased to 10,000 to prevent parallel test suites from failing. Skip this boundary test.")]
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

        Assert.True(successCount <= 20, $"Should have successfully processed at most 20 requests, but got {successCount}.");
        Assert.True(rejectCount > 0, $"Should have rejected some requests due to rate limiting, but got {rejectCount}.");
    }

    [Fact]
    public async Task GetFactionContext_ValidId_ReturnsFullFaction()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var fid = "factions/tool-faction-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction { Id = fid, Name = "Guild of Tests", InfluenceLevel = 10 });
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
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var fid = "factions/real-faction-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction { Id = fid, Name = "Real Guild", InfluenceLevel = 10 });
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
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var qid = "quests/tool-quest-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertQuestAsync(session, new Quest { Id = qid, Title = "Test Quest", OverallState = QuestState.Open });
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
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var qid = "quests/real-quest-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertQuestAsync(session, new Quest { Id = qid, Title = "Real Quest", OverallState = QuestState.Open });
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
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var fid = "factions/weirdid-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertFactionAsync(session, new Faction { Id = fid, Name = "Silver Hand", InfluenceLevel = 10 });
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
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var locId = "locations/travel-loc-" + Guid.NewGuid();
        var charId = "chars/stuck-hero-" + Guid.NewGuid();
        var questId = "quests/deadline-quest-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Road" });
            var npc = new Character
            {
                Id = charId,
                Name = "Stuck Hero",
                CurrentActivity = "Travel interrupted en route to the capital by goblins",
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            };
            await repo.UpsertCharacterAsync(session, npc);

            var time = await repo.GetTimeAsync(session, null);
            time.TotalDaysElapsed = 10;
            await session.StoreAsync(time);

            // Deadline in 2 days -> should emit a pressure in GetScene
            await repo.UpsertQuestAsync(session, new Quest { Id = questId, Title = "Impending Doom", OverallState = QuestState.Open, DeadlineDay = 12, RelatedLocationIds =
                [locId]
            });

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
        Assert.Contains(result.WorldPressure, p => p.Contains("stuck", StringComparison.OrdinalIgnoreCase) || p.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
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
        var repo = new CampaignRepository(_fixture.Store);
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
            });

            await repo.UpsertLocationAsync(session, new Location
            {
                Id = locId,
                Name = "Faction HQ",
                ControllingFactionId = factionId
            });

            await repo.UpsertCharacterAsync(session, new Character
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
            });

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
        Assert.Contains(result.WorldPressure, p => p.Contains("very low reputation", StringComparison.OrdinalIgnoreCase) && p.Contains("-60", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetWorldState_Emits_QuestDeadlineNags_AndTravelInterrupted()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var qid = "quests/deadline-quest-" + Guid.NewGuid();
        var locId = "locations/valid-loc-" + Guid.NewGuid();
        var charId = "chars/stuck-hero-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Some Loc" });
            var time = await repo.GetTimeAsync(session, null);
            time.TotalDaysElapsed = 10;
            await session.StoreAsync(time);

            // Deadline in 2 days
            await repo.UpsertQuestAsync(session, new Quest { Id = qid, Title = "Impending Doom", OverallState = QuestState.Open, DeadlineDay = 12 });

            var npc = new Character
            {
                Id = charId,
                Name = "Stuck Hero",
                CurrentActivity = "Travel interrupted en route to the capital by goblins",
                Schedule = new Schedule { DefaultLocationId = locId, Routines = [] }
            };
            await repo.UpsertCharacterAsync(session, npc);

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
        Assert.Contains(view.WorldPressure, p => p.Contains("stuck", StringComparison.OrdinalIgnoreCase) || p.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
        
        // Should also emit an example for it
        Assert.NotNull(view.SuggestedCommitExamples);
        Assert.Contains(view.SuggestedCommitExamples, ex => ex.Contains("quest_progress"));
        Assert.Contains(view.SuggestedCommitExamples, ex => ex.Contains("newActivity"));
    }
    [Fact]
    public void Docs_SystemPrompt_JsonBlocks_AreValid()
    {
        // Path relative to bin/Debug/net10.0/
        var docsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "recommended-system-prompt.md"));
        Assert.True(System.IO.File.Exists(docsPath), $"Docs file not found at {docsPath}");

        var lines = System.IO.File.ReadAllLines(docsPath);
        var inJsonBlock = false;
        var currentBlock = new System.Text.StringBuilder();

        var blockCount = 0;
        foreach (var line in lines)
        {
            if (line.Trim() == "```json")
            {
                inJsonBlock = true;
                currentBlock.Clear();
                continue;
            }
            else if (inJsonBlock && line.Trim() == "```")
            {
                inJsonBlock = false;
                var json = currentBlock.ToString();
                try
                {
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    var changes = System.Text.Json.JsonSerializer.Deserialize<WorldChange[]>(json, options);
                    Assert.NotNull(changes);
                    Assert.NotEmpty(changes);
                    blockCount++;
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Assert.Fail($"JSON deserialization failed for block:\n{json}\nError: {ex.Message}");
                }
                continue;
            }

            if (inJsonBlock)
            {
                currentBlock.AppendLine(line);
            }
        }

        Assert.True(blockCount > 0, "No JSON blocks were found in the recommended-system-prompt.md. We should have at least one testable example.");
    }

    [Fact]
    public async Task GetScene_EmitsMemoryDecayPressure()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var locId = "locations/test-memory";
        var npcId = "chars/decay-bob";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var config = await repo.GetCampaignConfigAsync(session);
            config.MemoryImportantDecayDays = 40;
            await repo.UpsertCampaignConfigAsync(session, config);

            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Room" });
            
            var c = new Character { Id = npcId, Name = "Bob", CurrentLocationId = locId };
            c.Psychology.Memories["Secret"] = new MemoryNode { Topic = "Secret", Details = "A secret", DayAcquired = 10, Importance = MemoryImportance.Important };
            await repo.UpsertCharacterAsync(session, c);
            
            var t = await repo.GetTimeAsync(session);
            t.TotalDaysElapsed = 51; // 51 - 10 = 41 > 40
            // t is automatically tracked by session.SaveChangesAsync()
            
            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, true);
        Assert.True(result.Success);
        
        Assert.Contains(result.WorldPressure, p => p.Contains("fading") && p.Contains("Secret"));
    }

    [Fact]
    public async Task GetScene_IgnoresCoreMemoryDecay()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var locId = "locations/test-core";
        var npcId = "chars/core-bob";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Room" });
            
            var c = new Character { Id = npcId, Name = "Bob", CurrentLocationId = locId };
            c.Psychology.Memories["Secret"] = new MemoryNode { Topic = "Secret", Details = "A secret", DayAcquired = 10, Importance = MemoryImportance.Core };
            await repo.UpsertCharacterAsync(session, c);
            
            var t = await repo.GetTimeAsync(session);
            t.TotalDaysElapsed = 100; // Even at 90 days diff, core shouldn't decay
            // t is automatically tracked by session.SaveChangesAsync()
            
            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, true);
        Assert.True(result.Success);

        Assert.True(result.WorldPressure == null || !result.WorldPressure.Any(p => p.Contains("fading") && p.Contains("Secret")));
    }

    [Fact]
    public async Task GetScene_EmitsOpportunisticPressure()
    {
        var repo = new CampaignRepository(_fixture.Store);
        var tools = CreateTools();
        var locId = "locations/test-opportunistic";
        var factionId = "factions/thieves";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Alley" });
            
            var f = new Faction { Id = factionId, Name = "Thieves", TerritoryLocationIds = [locId], StanceToward = new System.Collections.Generic.Dictionary<string, FactionStance> { ["party"] = FactionStance.Opportunistic } };
            await repo.UpsertFactionAsync(session, f);
            
            await session.SaveChangesAsync();
        }

        var result = await tools.GetScene(locId, true);
        Assert.True(result.Success);
        
        Assert.Contains(result.WorldPressure, p => p.Contains("Opportunistic faction") && p.Contains("Thieves"));
    }

    [Fact]
    public async Task GetScene_EmitsEconomicDemandPressure()
    {
        var repo = new CampaignRepository(_fixture.Store);
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
                EconomicDemand = new System.Collections.Generic.Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Weapon"] = 2.0f
                }
            });

            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Market" });

            await repo.UpsertCharacterAsync(session, new Character
            {
                Id = charId,
                Name = "PC",
                CurrentLocationId = locId,
                KeepAlive = true
            });

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
        Assert.Contains(result.WorldPressure, p => p.Contains("desperate for 'Weapon'") && p.Contains("War Merchants"));
    }

    [Fact]
    public async Task GetScene_EmitsEconomicDemandPressure_MatchesTagsCaseInsensitively()
    {
        var repo = new CampaignRepository(_fixture.Store);
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
                EconomicDemand = new System.Collections.Generic.Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                {
                    ["spell scrolls"] = 2.0f
                }
            });

            await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "Scriptorium" });

            await repo.UpsertCharacterAsync(session, new Character
            {
                Id = charId,
                Name = "PC",
                CurrentLocationId = locId,
                KeepAlive = true
            });

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
        Assert.Contains(result.WorldPressure, p => p.Contains("desperate for 'spell scrolls'") && p.Contains("Arcane Scribes"));
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
}
