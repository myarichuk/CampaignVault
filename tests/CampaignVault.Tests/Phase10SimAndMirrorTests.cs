using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Initiative;
using CampaignVault.Models;
using CampaignVault.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase10SimAndMirrorTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;
    private readonly CampaignDocumentKeys _keys = new();

    public Phase10SimAndMirrorTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private CampaignRepository CreateSimRepo(bool includeRelationalRearm = false)
    {
        var rules = new List<ISimulationRule>
        {
            new NeedsAccumulationRule(),
            new NeedConflictRule(),
            new MemorySalienceDecayRule()
        };
        if (includeRelationalRearm)
        {
            rules.Add(new RelationalRearmRule(
                new CampaignInitiativeSuppressionStore(),
                _keys));
        }

        var engine = new DefaultSimulationEngine(rules);
        return new CampaignRepository(
            _fixture.Store,
            engine,
            NullLogger<CampaignRepository>.Instance,
            new DefaultBehaviorSynthesizer(),
            _keys,
            initiativeService: InitiativeServiceFactory.CreateDefault());
    }

    private async Task SeedCampaignAsync(string campaignName, int day = 10, Action<CampaignConfig>? configure = null)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Campaign { Id = _keys.Meta(campaignName), Name = campaignName, DisplayName = campaignName });
        var config = new CampaignConfig { Id = _keys.Config(campaignName) };
        configure?.Invoke(config);
        await session.StoreAsync(config);
        await session.StoreAsync(new CampaignTime { Id = _keys.StateTime(campaignName), TotalDaysElapsed = day });
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task NeedConflictRule_SetsFlag_OnAdvanceWorld()
    {
        const string campaign = "need-conflict-sim";
        await SeedCampaignAsync(campaign);
        var repo = CreateSimRepo();
        using var session = _fixture.Store.OpenAsyncSession();

        var charId = "chars/on-duty";
        await session.StoreAsync(new Character
        {
            Id = charId,
            Name = "Barkeep",
            CampaignName = campaign,
            CurrentActivity = "tending bar",
            Schedule = new Schedule { DefaultLocationId = "locs/tavern", Routines = [] },
            Needs = new NeedsProfile
            {
                ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 85f }
            }
        });
        await session.SaveChangesAsync();
        await session.Advanced.AsyncDocumentQuery<Character>().WaitForNonStaleResults(TimeSpan.FromSeconds(5)).ToListAsync();

        await repo.AdvanceWorldAsync(session, 1, TimeOfDay.Dawn, campaign);
        await session.SaveChangesAsync();

        var reloaded = await session.LoadAsync<Character>(charId);
        Assert.True(reloaded!.Needs.ActivityConflictActive);
        Assert.Equal("tiredness", reloaded.Needs.ActivityConflictNeed);
    }

    [Fact]
    public async Task NeedConflict_SimThenContext_EmitsNeedInitiative()
    {
        const string campaign = "need-conflict-initiative";
        await SeedCampaignAsync(campaign);
        var repo = CreateSimRepo();
        using var session = _fixture.Store.OpenAsyncSession();

        var conflictId = "chars/conflict";
        var calmId = "chars/calm";
        await session.StoreAsync(new Character
        {
            Id = conflictId,
            Name = "On Duty",
            CampaignName = campaign,
            CurrentActivity = "tending bar",
            Schedule = new Schedule { DefaultLocationId = "locs/tavern", Routines = [] },
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 85f } }
        });
        await session.StoreAsync(new Character
        {
            Id = calmId,
            Name = "Resting",
            CampaignName = campaign,
            CurrentActivity = "resting",
            Schedule = new Schedule { DefaultLocationId = "locs/tavern", Routines = [] },
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 85f } }
        });
        await session.SaveChangesAsync();
        await session.Advanced.AsyncDocumentQuery<Character>().WaitForNonStaleResults(TimeSpan.FromSeconds(5)).ToListAsync();

        await repo.AdvanceWorldAsync(session, 1, TimeOfDay.Dawn, campaign);
        await session.SaveChangesAsync();

        var conflictNpc = await session.LoadAsync<Character>(conflictId);
        var enrichment = await repo.EnrichNpcInitiativeAsync(
            session, conflictNpc!, campaign, "get_npc_context", includeTensionBreakdown: true);

        Assert.Contains(enrichment.ActiveInitiatives, i => i.Driver == InitiativeDriver.Need);
        Assert.True(enrichment.TensionComponents!.NeedStress >= 85);

        var calmNpc = await session.LoadAsync<Character>(calmId);
        Assert.False(calmNpc!.Needs.ActivityConflictActive);
        var calmEnrichment = await repo.EnrichNpcInitiativeAsync(
            session, calmNpc, campaign, "get_npc_context", includeTensionBreakdown: true);
        Assert.DoesNotContain(calmEnrichment.ActiveInitiatives, i => i.Driver == InitiativeDriver.Need);
    }

    [Fact]
    public void MemorySalienceDecayRule_ReducesSalience_AndBumpsUrgency()
    {
        var rule = new MemorySalienceDecayRule();
        var npc = new Character
        {
            Id = "chars/mem",
            Name = "Mem NPC",
            Schedule = new Schedule { DefaultLocationId = "locs/1", Routines = [] },
            Psychology = new PsychologyProfile
            {
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Old"] = new MemoryNode
                    {
                        Topic = "Old",
                        Details = "Stale memory",
                        Salience = 0.8,
                        Importance = MemoryImportance.Important,
                        DayAcquired = 1,
                        Urgency = MemoryUrgency.Normal
                    }
                }
            }
        };

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 30 },
            [],
            [npc],
            null!,
            DaysPassed: 5,
            Config: new CampaignConfig { MemoryImportantDecayDays = 40 });

        var result = rule.ApplyAsync(context).GetAwaiter().GetResult();
        var memory = npc.Psychology.Memories["Old"];

        Assert.True(memory.Salience < 0.8);
        Assert.Equal(MemoryUrgency.High, memory.Urgency);
        Assert.NotEmpty(result.NarrativeEvents);
    }

    [Fact]
    public void MemorySalienceDecayRule_ZeroDecayDays_UsesMinimumStaleThreshold()
    {
        var rule = new MemorySalienceDecayRule();
        var npc = new Character
        {
            Id = "chars/zero-config",
            Name = "Zero Config NPC",
            Psychology = new PsychologyProfile
            {
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Fresh"] = new MemoryNode
                    {
                        Topic = "Fresh",
                        Salience = 0.8,
                        Importance = MemoryImportance.Important,
                        DayAcquired = 0,
                        Urgency = MemoryUrgency.Normal
                    }
                }
            }
        };

        var context = new SimulationContext(
            new CampaignTime { TotalDaysElapsed = 1 },
            [],
            [npc],
            null!,
            DaysPassed: 1,
            Config: new CampaignConfig { MemoryImportantDecayDays = 0 });

        rule.ApplyAsync(context).GetAwaiter().GetResult();

        Assert.Equal(MemoryUrgency.Normal, npc.Psychology.Memories["Fresh"].Urgency);
    }

    [Fact]
    public async Task UrgentInitiative_AppearsInGetSceneWorldPressure()
    {
        const string campaign = "urgent-mirror";
        await SeedCampaignAsync(campaign);
        using var session = _fixture.Store.OpenAsyncSession();

        var locId = "locs/market";
        var npcId = "chars/trauma";
        await session.StoreAsync(new Location { Id = locId, Name = "Market", CampaignName = campaign, VisualTags = ["market"] });
        await session.StoreAsync(new Character
        {
            Id = npcId,
            Name = "Guard",
            CampaignName = campaign,
            CurrentLocationId = locId,
            Schedule = new Schedule { DefaultLocationId = locId, Routines = [] },
            Psychology = new PsychologyProfile
            {
                Memories = new Dictionary<string, MemoryNode>
                {
                    ["Market violence"] = new MemoryNode
                    {
                        Topic = "Market violence",
                        Details = "Witnessed a brawl in the Market.",
                        Salience = 0.9,
                        Valence = EmotionalValence.Traumatic,
                        Urgency = MemoryUrgency.Urgent,
                        DayAcquired = 9
                    }
                }
            }
        });
        await session.SaveChangesAsync();
        session.Advanced.WaitForIndexesAfterSaveChanges(
            timeout: TimeSpan.FromSeconds(10),
            throwOnTimeout: true,
            indexes: ["Character/Search"]);

        var tools = TestCampaignToolsFactory.Create(_fixture.Store);
        var result = await tools.GetScene(locId, partyPresent: false, campaignName: campaign);

        Assert.True(result.Success);
        var npc = result.Data!.PresentNPCs.Single();
        Assert.Contains(npc.ActiveInitiatives, i => i.Urgency >= MemoryUrgency.High);
        Assert.NotNull(result.WorldPressure);
        Assert.Contains(result.WorldPressure!, p => p.Contains("Guard", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.WorldPressure!, p => p.Contains("NARRATIVE PROMPT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RelationalRearmRule_ReSurfacesAffection_AfterInterval()
    {
        const string campaign = "relational-rearm";
        const int rearmInterval = 3;
        await SeedCampaignAsync(campaign, day: 10, configure: c => c.RelationalRearmIntervalDays = rearmInterval);

        var repo = CreateSimRepo(includeRelationalRearm: true);
        using var session = _fixture.Store.OpenAsyncSession();

        var locId = "locs/inn";
        var npcId = "chars/barliman";
        var pcId = "chars/pc1";
        await session.StoreAsync(new Location { Id = locId, Name = "Inn", CampaignName = campaign });
        await session.StoreAsync(new Character
        {
            Id = npcId,
            Name = "Barliman",
            CampaignName = campaign,
            CurrentLocationId = locId,
            Schedule = new Schedule { DefaultLocationId = locId, Routines = [] },
            Social = new SocialProfile
            {
                Relationships = new Dictionary<string, int> { [pcId] = 85 }
            }
        });
        await session.StoreAsync(new Character
        {
            Id = pcId,
            Name = "Aldric",
            CampaignName = campaign,
            CurrentLocationId = locId
        });
        await session.SaveChangesAsync();
        await session.Advanced.AsyncDocumentQuery<Character>().WaitForNonStaleResults(TimeSpan.FromSeconds(5)).ToListAsync();

        var npc = await session.LoadAsync<Character>(npcId);
        var pc = await session.LoadAsync<Character>(pcId);
        var present = new List<Character> { npc!, pc! };

        var first = await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", includeTensionBreakdown: true, presentEntities: present);
        await session.SaveChangesAsync();

        Assert.Contains(first.ActiveInitiatives, i => i.Key.StartsWith("affection:", StringComparison.Ordinal));

        var second = await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", includeTensionBreakdown: true, presentEntities: present);
        Assert.Empty(second.ActiveInitiatives);

        await repo.AdvanceWorldAsync(session, rearmInterval, TimeOfDay.Morning, campaign);
        await session.SaveChangesAsync();

        npc = await session.LoadAsync<Character>(npcId);
        var third = await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", includeTensionBreakdown: true, presentEntities: present);

        Assert.Contains(third.ActiveInitiatives, i => i.Key.StartsWith("affection:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelationalRearmRule_DoesNotRearm_BeforeInterval()
    {
        const string campaign = "relational-rearm-early";
        await SeedCampaignAsync(campaign, day: 10, configure: c => c.RelationalRearmIntervalDays = 7);

        var repo = CreateSimRepo(includeRelationalRearm: true);
        using var session = _fixture.Store.OpenAsyncSession();

        var locId = "locs/inn";
        var npcId = "chars/barliman";
        var pcId = "chars/pc1";
        await session.StoreAsync(new Character
        {
            Id = npcId,
            Name = "Barliman",
            CampaignName = campaign,
            CurrentLocationId = locId,
            Schedule = new Schedule { DefaultLocationId = locId, Routines = [] },
            Social = new SocialProfile
            {
                Relationships = new Dictionary<string, int> { [pcId] = 85 }
            }
        });
        await session.StoreAsync(new Character { Id = pcId, Name = "Aldric", CampaignName = campaign });
        await session.SaveChangesAsync();
        await session.Advanced.AsyncDocumentQuery<Character>().WaitForNonStaleResults(TimeSpan.FromSeconds(5)).ToListAsync();

        var npc = await session.LoadAsync<Character>(npcId);
        var pc = await session.LoadAsync<Character>(pcId);
        var present = new List<Character> { npc!, pc! };

        await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", includeTensionBreakdown: true, presentEntities: present);
        await session.SaveChangesAsync();

        await repo.AdvanceWorldAsync(session, 2, TimeOfDay.Morning, campaign);
        await session.SaveChangesAsync();

        npc = await session.LoadAsync<Character>(npcId);
        var afterShortAdvance = await repo.EnrichNpcInitiativeAsync(
            session, npc!, campaign, "get_npc_context", includeTensionBreakdown: true, presentEntities: present);

        Assert.Empty(afterShortAdvance.ActiveInitiatives);
    }
}