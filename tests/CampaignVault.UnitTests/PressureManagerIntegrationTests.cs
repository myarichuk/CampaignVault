using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
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

    [Fact]
    public async Task GetScene_CapsPressures_WhenMultipleIssuesExist()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaignName = "pressure-cap-test-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignName);
        var repo = _fixture.CreateRepository();

        var locId = "locations/test-room-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            // Create a campaign config with cap = 1
            await session.StoreAsync(new CampaignConfig { Id = new CampaignDocumentKeys().Config(campaignName), MaxPressuresPerResponse = 1 });
            
            // Create a location with multiple issues
            var loc = new Location
            {
                Id = locId,
                Name = "Buggy Room",
                Type = LocationType.Room, // missing exits -> 1 pressure
                // missing parent reverse link -> handled in getscene if parent exists, let's just use empty room
                // empty room expects crowd -> 1 pressure
                AmbientCrowd = "Should be a crowd here",
                CampaignName = campaignName
            };
            await session.StoreAsync(loc);
            
            // Missing parent
            loc.ParentLocationId = "locations/parent-" + Guid.NewGuid();
            var parent = new Location { Id = loc.ParentLocationId, Name = "Parent", CampaignName = campaignName };
            // Parent doesn't link back -> 1 pressure
            await session.StoreAsync(parent);
            
            await session.SaveChangesAsync();
        }

        // Without capping, this would emit 3 pressures:
        // 1. No Exits (Engine Warning)
        // 2. Broken reverse link (Engine Warning)
        // 3. Expected crowd missing (Narrative Prompt)
        
        var result = await tools.GetScene(locId, campaignName: campaignName);
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
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaignName = "pressure-nop-test-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignName);

        var locId = "locations/test-nop-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var loc = new Location
            {
                Id = locId,
                Name = "NOP Room",
                Type = LocationType.Room, // No exits -> 1 pressure
                CampaignName = campaignName
            };
            await session.StoreAsync(loc);
            await session.SaveChangesAsync();
        }

        // Config max defaults to 5. We have 2 pressures.
        var result1 = await tools.GetScene(locId, campaignName: campaignName);
        Assert.True(result1.Success);
        Assert.NotNull(result1.WorldPressure);
        Assert.True(result1.WorldPressure.Length >= 2); // It was processed but not truncated.

        // It should be stored in tracking for cooldown.
        // Wait, if it's stored in tracking, then on Day 1 it's surfaced.
        // If we call GetScene again on the same day, it should be SUPPRESSED.
        var result2 = await tools.GetScene(locId, campaignName: campaignName);
        Assert.True(result2.Success);
        Assert.Null(result2.WorldPressure); // Suppressed!
    }

    [Fact]
    public async Task StageChangesAsync_Commit_ClearsCooldown()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaignName = "pressure-clear-test-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignName);

        var locId = "locations/test-clear-" + Guid.NewGuid();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var loc = new Location
            {
                Id = locId,
                Name = "Clear Room",
                Type = LocationType.Room, // No exits -> 1 pressure
                CampaignName = campaignName
            };
            await session.StoreAsync(loc);
            await session.StoreAsync(new Campaign { Name = campaignName, Id = new CampaignDocumentKeys().Meta(campaignName) });
            await session.SaveChangesAsync();
        }

        // 1. Initial read -> Surface pressure
        var result1 = await tools.GetScene(locId, campaignName: campaignName);
        Assert.NotNull(result1.WorldPressure);
        Assert.True(result1.WorldPressure.Length >= 2);

        // 2. Second read -> Suppressed
        var result2 = await tools.GetScene(locId, campaignName: campaignName);
        Assert.Null(result2.WorldPressure);

        // 3. Commit a fix!
        var commitRes = await tools.Commit([
            new LocationUpdate { LocationId = locId, Name = "Fixed Room", Description = "Added via commit" }
        ], "Fixed room", campaignName);
        Assert.True(commitRes.Success);

        // 4. Third read -> The tracking should have been cleared, so if the pressure is still there, it surfaces again.
        // (Our LocationUpdate didn't fix the lack of exits, so the pressure still exists)
        var result3 = await tools.GetScene(locId, campaignName: campaignName);
        Assert.NotNull(result3.WorldPressure);
        Assert.True(result3.WorldPressure.Length >= 2);
    }

    /// <summary>
    /// Regression guard for the PressureManager key-collision fix: a character with no ruleset
    /// stats trips two independent EngineWarning contributors —
    /// IncompleteSystemStatsPressureContributor ("uninitialized systemStats") and
    /// CharacterDistressPressureContributor ("no MaxHp set") — both keyed only by Severity:EntityId
    /// before this fix. Since their Text (and thus signature) differ, each call's cooldown write from
    /// one contributor made the other look "changed" to the next call, so the pair perpetually
    /// re-surfaced instead of respecting PressureCooldownDays. Two consecutive pure-query take_turn
    /// calls (no changes at all) must suppress on the second call.
    /// </summary>
    [Fact]
    public async Task PureQuery_TwiceInARow_SuppressesEvenWithCollidingContributors()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaignName = "pressure-collision-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignName);
        var repo = _fixture.CreateRepository();

        var npcId = $"chars/{campaignName}-owen";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, campaignName);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = npcId, Name = "Owen", KeepAlive = true });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, campaignName);
        Assert.True(seed.Success, seed.Summary);
        var seedPressure = (seed.Data!.Mode == TurnMode.Full ? seed.Data.WorldState?.WorldPressure : seed.Data.WorldStateDelta?.WorldPressure)
            ?.FirstOrDefault(p => p.Contains(npcId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(seedPressure);

        var second = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, campaignName);
        var secondPressure = (second.Data!.Mode == TurnMode.Full ? second.Data.WorldState?.WorldPressure : second.Data.WorldStateDelta?.WorldPressure)
            ?.FirstOrDefault(p => p.Contains(npcId, StringComparison.OrdinalIgnoreCase));
        Assert.Null(secondPressure);
    }

    /// <summary>
    /// Regression guard for the StageChangesAsync cooldown-clearing fix: a character merely
    /// *mentioned* by a [NarrativeOnly] change (event, activity) shouldn't have their pending
    /// pressure cooldown reset — only a structural change (character_update, etc.) that could
    /// plausibly have fixed the underlying issue should. Before the fix, EventOccurred/ActivityChange
    /// referencing an NPC cleared their cooldown every turn, so an EngineWarning like "uninitialized
    /// systemStats" nagged on every single conversational beat instead of once per
    /// PressureCooldownDays.
    /// </summary>
    [Fact]
    public async Task NarrativeOnlyChanges_DontClearPressureCooldown_ButStructuralChangesDo()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaignName = "pressure-narrative-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignName);
        var repo = _fixture.CreateRepository();

        var locId = $"locations/{campaignName}-tavern";
        var npcId = $"chars/{campaignName}-owen";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, campaignName);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Tavern" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = npcId, Name = "Owen", KeepAlive = true, CurrentLocationId = locId });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Turn(WorldChange[]? changes) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = changes != null ? "Beat." : null,
            IncludeWorldState = true
        }, campaignName);

        string? PressureFor(ToolResult<TurnResult> r) =>
            (r.Data!.Mode == TurnMode.Full ? r.Data.WorldState?.WorldPressure : r.Data.WorldStateDelta?.WorldPressure)
            ?.FirstOrDefault(p => p.Contains(npcId, StringComparison.OrdinalIgnoreCase));

        // Call 1 (Full, seed): the uninitialized-systemStats EngineWarning surfaces for Owen.
        var seed = await Turn(null);
        Assert.True(seed.Success, seed.Summary);
        Assert.NotNull(PressureFor(seed));

        // Call 2: purely narrative changes name Owen (event.involved, activity.characterId) but fix
        // nothing about him -> cooldown must NOT clear -> warning stays suppressed.
        var narrative = await Turn([
            new EventOccurred { Summary = "Idle chatter at the bar.", Category = EventCategory.Discovery, Involved = [npcId] },
            new ActivityChange { CharacterId = npcId, NewActivity = "Sweeping the floor" }
        ]);
        Assert.True(narrative.Success, narrative.Summary);
        Assert.Null(PressureFor(narrative));

        // Call 3: a structural change actually touches Owen -> cooldown clears -> warning resurfaces
        // (still unresolved, since KeepAlive alone doesn't bootstrap stats).
        var structural = await Turn([
            new CharacterUpdate { CharacterId = npcId, KeepAlive = true }
        ]);
        Assert.True(structural.Success, structural.Summary);
        Assert.NotNull(PressureFor(structural));
    }

    [Fact]
    public async Task FilterAndCapAsync_BatchesSimilarAlerts()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var campaignName = "pressure-batch-test-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, campaignName);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var config = new CampaignConfig { Id = new CampaignDocumentKeys().Config(campaignName), MaxPressuresPerResponse = 50 };
            await session.StoreAsync(config);

            // Add 3 characters who are all starving (same GroupingKey)
            for (var i = 1; i <= 3; i++)
            {
                var charId = $"characters/batch-test-{i}";
                var c = new Character
                {
                    Id = charId,
                    Name = $"Batch Char {i}",
                    CurrentHp = 10,
                    MaxHp = 10,
                    CampaignName = campaignName,
                    KeepAlive = true,
                    SystemStats = new Dnd5eExtension
                    {
                        ArmorClass = 12,
                        Dexterity = 12,
                        SkillModifiers = new Dictionary<string, int> { { "Survival", 2 } }
                    }
                };
                c.Needs.ActiveNeeds["hunger"] = 95f;
                await session.StoreAsync(c);
            }

            var uniqueChar = new Character { 
                Id = "characters/batch-test-unique", 
                Name = "Unique Issue Char", 
                CurrentHp = 10, MaxHp = 10, 
                CampaignName = campaignName, 
                KeepAlive = true,
                SystemStats = new Dnd5eExtension
                {
                    ArmorClass = 12,
                    Constitution = 12,
                    SkillModifiers = new Dictionary<string, int> { { "Arcana", 1 } },
                    StatusEffects = [new StatusEffect { Name = "Super Unique Curse", Category = "Curse" }]
                }
            };
            await session.StoreAsync(uniqueChar);

            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(5));
            await session.SaveChangesAsync();
        }

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
            var items1 = await pm.FilterAndCapAsync(session, campaignName, 1, pressures);
            await session.SaveChangesAsync();
            var p1 = PressureManager.ToDisplayStrings(items1);
            Assert.Single(items1);
            Assert.Contains("NARRATIVE PROMPT", p1[0]);
            Assert.DoesNotContain("ESCALATED", p1[0]);
        }

        // Within cooldown (Day 2) -> Suppressed
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var itemsSup = await pm.FilterAndCapAsync(session, campaignName, 2, pressures);
            await session.SaveChangesAsync();
            Assert.Empty(itemsSup);
        }

        // Cycle 2 (Day 4) - surfaces again (suppression count becomes 1)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var items2 = await pm.FilterAndCapAsync(session, campaignName, 4, pressures);
            await session.SaveChangesAsync();
            var p2 = PressureManager.ToDisplayStrings(items2);
            Assert.Single(items2);
            Assert.Contains("NARRATIVE PROMPT", p2[0]);
        }

        // Cycle 3 (Day 7) - surfaces again (suppression count becomes 2)
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var items3 = await pm.FilterAndCapAsync(session, campaignName, 7, pressures);
            await session.SaveChangesAsync();
            var p3 = PressureManager.ToDisplayStrings(items3);
            Assert.Single(items3);
            Assert.Contains("NARRATIVE PROMPT", p3[0]);
        }

        // Cycle 4 (Day 10) - surfaces again (suppression count becomes 3) -> ESCALATED!
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var items4 = await pm.FilterAndCapAsync(session, campaignName, 10, pressures);
            await session.SaveChangesAsync();
            var p4 = PressureManager.ToDisplayStrings(items4);
            Assert.Single(items4);
            Assert.Contains("ENGINE WARNING", p4[0]);
            // Escalation is indicated by bumping to EngineWarning severity (detailed [ESCALATED] note optional in display)
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

        List<WorldPressureItem> cappedItems;
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            cappedItems = await manager.FilterAndCapAsync(session, campName, 10, rawPressures);
            await session.SaveChangesAsync();
        }
        var formatted = PressureManager.ToDisplayStrings(cappedItems);

        // Assert 5 items returned
        Assert.Equal(5, cappedItems.Count);

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
            Assert.True(camp.PressureCooldowns.ContainsKey($"{PressureSeverity.EngineWarning}:Travel:Interrupted:chars/2")); // Travel
            Assert.True(camp.PressureCooldowns.ContainsKey($"{PressureSeverity.EngineWarning}:Location:MissingData:locs/1"));  // Location

            // The exact missing two could be any of the Narrative ones (since they sort stably but might be arbitrary).
            // But we can assert 2 are missing.
        }
    }

    [Fact]
    public async Task FilterAndCapAsync_SameSignature_DifferingOnlyByDigits_StillSuppressedByCooldown()
    {
        var campaignName = "pressure-signature-same-" + Guid.NewGuid();
        var keys = new CampaignDocumentKeys();
        var pm = new PressureManager(keys);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Name = campaignName, Id = keys.Meta(campaignName) });
            await session.StoreAsync(new CampaignConfig { Id = keys.Config(campaignName), PressureCooldownDays = 3, PressureEscalationCount = 3 });
            await session.SaveChangesAsync();
        }

        // Day 1: morale at 8% — surfaces.
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var pressures = new[] { new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Morale is at 8%.", "Character:Morale") };
            var items = await pm.FilterAndCapAsync(session, campaignName, 1, pressures);
            await session.SaveChangesAsync();
            Assert.Single(items);
        }

        // Day 2 (within cooldown): morale dropped to 3% — same underlying nag (digits stripped
        // before hashing), still suppressed rather than surfacing as "new".
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var pressures = new[] { new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Morale is at 3%.", "Character:Morale") };
            var items = await pm.FilterAndCapAsync(session, campaignName, 2, pressures);
            await session.SaveChangesAsync();
            Assert.Empty(items);
        }
    }

    [Fact]
    public async Task FilterAndCapAsync_DifferentSignature_SurfacesDespiteCooldown_AndResetsEscalation()
    {
        var campaignName = "pressure-signature-diff-" + Guid.NewGuid();
        var keys = new CampaignDocumentKeys();
        var pm = new PressureManager(keys);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Campaign { Name = campaignName, Id = keys.Meta(campaignName) });
            await session.StoreAsync(new CampaignConfig { Id = keys.Config(campaignName), PressureCooldownDays = 3, PressureEscalationCount = 3 });
            await session.SaveChangesAsync();
        }

        // Day 1: "starving" nag surfaces.
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var pressures = new[] { new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is starving.", "Character:Morale") };
            var items = await pm.FilterAndCapAsync(session, campaignName, 1, pressures);
            await session.SaveChangesAsync();
            Assert.Single(items);
        }

        // Day 2 (within cooldown), same Severity:EntityId key but materially different text ("dehydrated"
        // instead of "starving") — must NOT be silently suppressed by the stale cooldown, since the
        // underlying issue changed. Also confirmed non-escalated (fresh cycle, not inheriting count).
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var pressures = new[] { new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is dehydrated.", "Character:Morale") };
            var items = await pm.FilterAndCapAsync(session, campaignName, 2, pressures);
            await session.SaveChangesAsync();
            var displayed = PressureManager.ToDisplayStrings(items);
            Assert.Single(items);
            Assert.Contains("NARRATIVE PROMPT", displayed[0]);
            Assert.DoesNotContain("ENGINE WARNING", displayed[0]);
        }
    }

    [Fact]
    public async Task FilterAndCapAsync_PreExistingCooldownWithoutSignature_DeserializesAndBehavesAsSuppressed()
    {
        var campaignName = "pressure-signature-legacy-" + Guid.NewGuid();
        var keys = new CampaignDocumentKeys();
        var pm = new PressureManager(keys);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var campaign = new Campaign { Name = campaignName, Id = keys.Meta(campaignName) };
            // Simulate a pre-existing cooldown entry written before LastSignature existed.
            campaign.PressureCooldowns[$"{PressureSeverity.NarrativePrompt}:Character:Morale:chars/1"] = new PressureState(1, 0);
            await session.StoreAsync(campaign);
            await session.StoreAsync(new CampaignConfig { Id = keys.Config(campaignName), PressureCooldownDays = 3, PressureEscalationCount = 3 });
            await session.SaveChangesAsync();
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var pressures = new[] { new WorldPressureItem(PressureSeverity.NarrativePrompt, "chars/1", "Character is starving.", "Character:Morale") };
            var items = await pm.FilterAndCapAsync(session, campaignName, 2, pressures);
            await session.SaveChangesAsync();
            // Null LastSignature => treated as "no prior signature to compare" => normal cooldown applies.
            Assert.Empty(items);
        }
    }
}
