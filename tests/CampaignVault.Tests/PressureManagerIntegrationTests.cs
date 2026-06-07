using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class PressureManagerIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public PressureManagerIntegrationTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignTools CreateTools(ICurrentCampaignContext context)
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
            context
        );
    }

    [Fact]
    public async Task GetScene_CapsPressures_WhenMultipleIssuesExist()
    {
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);
        await tools.SelectCampaign("pressure-cap-test-" + Guid.NewGuid());
        var repo = new CampaignRepository(_fixture.Store);

        var locId = "locations/test-room-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            // Create a campaign config with cap = 1
            await session.StoreAsync(new CampaignConfig { Id = new CampaignDocumentKeys().Config(context.CurrentCampaignName), MaxPressuresPerResponse = 1 });
            
            // Create a location with multiple issues
            var loc = new Location
            {
                Id = locId,
                Name = "Buggy Room",
                Type = LocationType.Room, // missing exits -> 1 pressure
                // missing parent reverse link -> handled in getscene if parent exists, let's just use empty room
                // empty room expects crowd -> 1 pressure
                AmbientCrowd = "Should be a crowd here",
                CampaignName = context.CurrentCampaignName
            };
            await session.StoreAsync(loc);
            
            // Missing parent
            loc.ParentLocationId = "locations/parent-" + Guid.NewGuid();
            var parent = new Location { Id = loc.ParentLocationId, Name = "Parent", CampaignName = context.CurrentCampaignName };
            // Parent doesn't link back -> 1 pressure
            await session.StoreAsync(parent);
            
            await session.SaveChangesAsync();
        }

        // Without capping, this would emit 3 pressures:
        // 1. No Exits (Engine Warning)
        // 2. Broken reverse link (Engine Warning)
        // 3. Expected crowd missing (Narrative Prompt)
        
        var result = await tools.GetScene(locId);
        Assert.True(result.Success);
        
        // Config cap is 1, so we should only get 1 pressure!
        Assert.NotNull(result.WorldPressure);
        Assert.Single(result.WorldPressure);
        
        // It should prioritize the Engine Warning
        Assert.Contains("ENGINE WARNING", result.WorldPressure[0]);
    }

    [Fact]
    public async Task FilterAndCapAsync_NOP_IfBelowThreshold_AndNoSuppression()
    {
        // Add another case for FilterAndCapAsync being NOP if below threshold
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);
        await tools.SelectCampaign("pressure-nop-test-" + Guid.NewGuid());

        var locId = "locations/test-nop-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var loc = new Location
            {
                Id = locId,
                Name = "NOP Room",
                Type = LocationType.Room, // No exits -> 1 pressure
                CampaignName = context.CurrentCampaignName
            };
            await session.StoreAsync(loc);
            await session.SaveChangesAsync();
        }

        // Config max defaults to 5. We have 2 pressures.
        var result1 = await tools.GetScene(locId);
        Assert.True(result1.Success);
        Assert.NotNull(result1.WorldPressure);
        Assert.Equal(2, result1.WorldPressure.Length); // It was processed but not truncated.

        // It should be stored in tracking for cooldown.
        // Wait, if it's stored in tracking, then on Day 1 it's surfaced.
        // If we call GetScene again on the same day, it should be SUPPRESSED.
        var result2 = await tools.GetScene(locId);
        Assert.True(result2.Success);
        Assert.Null(result2.WorldPressure); // Suppressed!
    }

    [Fact]
    public async Task StageChangesAsync_Commit_ClearsCooldown()
    {
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);
        await tools.SelectCampaign("pressure-clear-test-" + Guid.NewGuid());

        var locId = "locations/test-clear-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var loc = new Location
            {
                Id = locId,
                Name = "Clear Room",
                Type = LocationType.Room, // No exits -> 1 pressure
                CampaignName = context.CurrentCampaignName
            };
            await session.StoreAsync(loc);
            await session.StoreAsync(new Campaign { Name = context.CurrentCampaignName, Id = new CampaignDocumentKeys().Meta(context.CurrentCampaignName) });
            await session.SaveChangesAsync();
        }

        // 1. Initial read -> Surface pressure
        var result1 = await tools.GetScene(locId);
        Assert.NotNull(result1.WorldPressure);
        Assert.Equal(2, result1.WorldPressure.Length);

        // 2. Second read -> Suppressed
        var result2 = await tools.GetScene(locId);
        Assert.Null(result2.WorldPressure);

        // 3. Commit a fix!
        var commitRes = await tools.Commit([
            new LocationUpdate { LocationId = locId, Name = "Fixed Room", Description = "Added via commit" }
        ], "Fixed room");
        Assert.True(commitRes.Success);

        // 4. Third read -> The tracking should have been cleared, so if the pressure is still there, it surfaces again.
        // (Our LocationUpdate didn't fix the lack of exits, so the pressure still exists)
        var result3 = await tools.GetScene(locId);
        Assert.NotNull(result3.WorldPressure);
        Assert.Equal(2, result3.WorldPressure.Length);
    }

    [Fact]
    public async Task FilterAndCapAsync_BatchesSimilarAlerts()
    {
        var context = new CurrentCampaignContext();
        var tools = CreateTools(context);
        var campaignName = "pressure-batch-test-" + Guid.NewGuid();
        await tools.SelectCampaign(campaignName);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var config = new CampaignConfig { Id = new CampaignDocumentKeys().Config(campaignName), MaxPressuresPerResponse = 50 };
            await session.StoreAsync(config);

            // Add 3 characters who are all starving (same GroupingKey)
            for (var i = 1; i <= 3; i++)
            {
                var charId = $"characters/batch-test-{i}";
                var c = new Character { Id = charId, Name = $"Batch Char {i}", CurrentHp = 10, MaxHp = 10, CampaignName = campaignName, KeepAlive = true };
                c.Needs.ActiveNeeds["hunger"] = 95f;
                await session.StoreAsync(c);
            }

            var uniqueChar = new Character { 
                Id = "characters/batch-test-unique", 
                Name = "Unique Issue Char", 
                CurrentHp = 10, MaxHp = 10, 
                CampaignName = campaignName, 
                KeepAlive = true,
                SystemStats = new Dnd5eExtension {
                    StatusEffects = [new StatusEffect { Name = "Super Unique Curse", Category = "Curse" }]
                }
            };
            await session.StoreAsync(uniqueChar);

            await session.SaveChangesAsync();
            await session.Advanced.AsyncDocumentQuery<Character>().WaitForNonStaleResults(TimeSpan.FromSeconds(5)).ToListAsync();
        }

        System.Threading.Thread.Sleep(500);
        var result = await tools.GetWorldState("locations/any", campaignName);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.WorldPressure);

        Assert.True(result.Data.WorldPressure.Count() >= 2);
        
        var allPressures = string.Join("\n", result.Data.WorldPressure);
        Console.WriteLine("ALL PRESSURES: " + allPressures);
        Assert.Contains("Unique Issue Char is suffering from Super Unique Curse", allPressures);

        var batchedString = result.Data.WorldPressure.First(s => s.Contains("Batch Char 1"));
        Assert.Contains("similar issues", batchedString);
        Assert.Contains("Need hunger", batchedString);
        Assert.Contains("Batch Char 2", batchedString);
        Assert.Contains("Batch Char 3", batchedString);
    }

    [Fact]
    public async Task FilterAndCapAsync_EscalatesAfterThreeCycles()
    {
        var campaignName = "pressure-escalate-test-" + Guid.NewGuid();
        var keys = new CampaignDocumentKeys();
        var pm = new PressureManager(keys);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Name = campaignName, Id = keys.Meta(campaignName) });
            await session.StoreAsync(new CampaignConfig { Id = keys.Config(campaignName), PressureCooldownDays = 3, PressureEscalationCount = 3 });
            await session.SaveChangesAsync();
        }

        var pressures = new[] { new WorldPressureItem(PressureSeverity.NarrativePrompt, "locations/test", "You should do X.", "test-issue") };

        // Cycle 1 (Day 1) - surfaces
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var p1 = await pm.FilterAndCapAsync(session, campaignName, 1, pressures);
            await session.SaveChangesAsync();
            Assert.Single(p1);
            Assert.Contains("NARRATIVE PROMPT", p1[0]);
            Assert.DoesNotContain("ESCALATED", p1[0]);
        }

        // Within cooldown (Day 2) -> Suppressed
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var p1_sup = await pm.FilterAndCapAsync(session, campaignName, 2, pressures);
            await session.SaveChangesAsync();
            Assert.Empty(p1_sup);
        }

        // Cycle 2 (Day 4) - surfaces again (suppression count becomes 1)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var p2 = await pm.FilterAndCapAsync(session, campaignName, 4, pressures);
            await session.SaveChangesAsync();
            Assert.Single(p2);
            Assert.Contains("NARRATIVE PROMPT", p2[0]);
        }

        // Cycle 3 (Day 7) - surfaces again (suppression count becomes 2)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var p3 = await pm.FilterAndCapAsync(session, campaignName, 7, pressures);
            await session.SaveChangesAsync();
            Assert.Single(p3);
            Assert.Contains("NARRATIVE PROMPT", p3[0]);
        }

        // Cycle 4 (Day 10) - surfaces again (suppression count becomes 3) -> ESCALATED!
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var p4 = await pm.FilterAndCapAsync(session, campaignName, 10, pressures);
            await session.SaveChangesAsync();
            Assert.Single(p4);
            Assert.Contains("ENGINE WARNING", p4[0]);
            Assert.Contains("ESCALATED", p4[0]);
        }
    }

    [Fact]
    public async Task PressureManager_Enforces_Hard_Cap_And_Severity_Ordering()
    {
        var campName = "pressure-cap-scenario-" + Guid.NewGuid();
        
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Id = new CampaignDocumentKeys().Meta(campName), Name = campName });
            await session.StoreAsync(new CampaignConfig { Id = new CampaignDocumentKeys().Config(campName), MaxPressuresPerResponse = 5 });
            await session.SaveChangesAsync();
        }

        var manager = new PressureManager(new CampaignDocumentKeys());

        // 7 distinct pressures
        var rawPressures = new[]
        {
            new WorldPressureItem(PressureSeverity.NarrativePrompt, "factions/1", "Reputation changed", "Faction:Reputation"),
            new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Orphaned item", "Character:OrphanedItem"),
            new WorldPressureItem(PressureSeverity.NarrativePrompt, "factions/2", "Presence expanded", "Faction:PresenceChange"),
            new WorldPressureItem(PressureSeverity.NarrativePrompt, "quests/1", "Quest stale", "Quest:Stale"),
            new WorldPressureItem(PressureSeverity.EngineWarning, "chars/2", "Travel interrupted", "Travel:Interrupted"),
            new WorldPressureItem(PressureSeverity.EngineWarning, "locs/1", "Location data missing", "Location:MissingData"),
            new WorldPressureItem(PressureSeverity.NarrativePrompt, "quests/2", "Quest ending soon", "Quest:ApproachingDeadline")
        };

        string[] formatted;
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            formatted = await manager.FilterAndCapAsync(session, campName, 10, rawPressures);
            await session.SaveChangesAsync();
        }

        // Assert 5 items returned
        Assert.Equal(5, formatted.Length);

        // Assert Engine Warnings are top 2
        Assert.Contains("Travel interrupted", formatted[0]);
        Assert.Contains("Location data missing", formatted[1]);

        // Assert the next 3 are Narrative Prompts from the input
        for(var i = 2; i < 5; i++)
        {
            Assert.Contains("NARRATIVE PROMPT", formatted[i]);
            // Ensure no engine warnings slipped down
            Assert.DoesNotContain("ENGINE WARNING", formatted[i]);
        }

        // Verify the discarded ones did NOT trigger cooldowns
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var camp = await session.LoadAsync<Campaign>(new CampaignDocumentKeys().Meta(campName));
            
            // Only 5 cooldowns should be registered
            Assert.Equal(5, camp.PressureCooldowns.Count);

            // Travel and Location MUST be registered
            Assert.True(camp.PressureCooldowns.ContainsKey($"{PressureSeverity.EngineWarning}:chars/2")); // Travel
            Assert.True(camp.PressureCooldowns.ContainsKey($"{PressureSeverity.EngineWarning}:locs/1"));  // Location

            // The exact missing two could be any of the Narrative ones (since they sort stably but might be arbitrary).
            // But we can assert 2 are missing.
        }
    }
}
