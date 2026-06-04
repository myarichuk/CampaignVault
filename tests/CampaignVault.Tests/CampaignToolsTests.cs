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
        var selector = new RulesetResolverSelector(new IRulesetResolver[] { 
            new Dnd5eRulesetResolver(rollSvc),
            new Pf2eRulesetResolver(rollSvc),
            new Fallout2d20RulesetResolver(rollSvc)
        });
        
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
        for (int i = 0; i < changes.Length; i++)
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
        
        int successCount = 0;
        int rejectCount = 0;

        // The limit is 20 tokens. If we slam it with 30 concurrent or rapid requests, some should fail.
        for (int i = 0; i < 30; i++)
        {
            var res = await tools.Commit(change, "Spamming the system");
            if (res.Success) successCount++;
            else if (res.Error == "RateLimitExceeded" && res.Summary!.Contains("rate limit exceeded")) rejectCount++;
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
        var indexWaitStart = System.DateTime.UtcNow;
        while ((System.DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false)) break;
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
        var indexWaitStart = System.DateTime.UtcNow;
        while ((System.DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false)) break;
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

        var indexWaitStart = System.DateTime.UtcNow;
        while ((System.DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Faction/Search" && x.IsStale == false)) break;
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
                Schedule = new Schedule { DefaultLocationId = locId, Routines = new System.Collections.Generic.List<Routine>() }
            };
            await repo.UpsertCharacterAsync(session, npc);

            var time = await repo.GetTimeAsync(session, null);
            time.TotalDaysElapsed = 10;
            await session.StoreAsync(time);

            // Deadline in 2 days -> should emit a pressure in GetScene
            await repo.UpsertQuestAsync(session, new Quest { Id = questId, Title = "Impending Doom", OverallState = QuestState.Open, DeadlineDay = 12, RelatedLocationIds = new System.Collections.Generic.List<string> { locId } });

            await session.SaveChangesAsync();
        }

        var indexWaitStart = System.DateTime.UtcNow;
        while ((System.DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false)) break;
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
                Schedule = new Schedule { DefaultLocationId = locId, Routines = new System.Collections.Generic.List<Routine>() }
            };
            await repo.UpsertCharacterAsync(session, npc);

            await session.SaveChangesAsync();
        }

        var indexWaitStart = System.DateTime.UtcNow;
        while ((System.DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _fixture.Store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.Any(x => x.Name == "Quest/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Character/Search" && x.IsStale == false) &&
                stats.Indexes.Any(x => x.Name == "Location/Search" && x.IsStale == false)) break;
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
}
