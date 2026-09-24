using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Middleware;
using CampaignVault.Models;
using CampaignVault.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SESSION_HANDOFF_PLAN.md: end_session stores a capped, model-authored handoff; start_session returns it
/// with engine facts read from the DB, at a size that does not grow with sessions played.
/// </summary>
[Collection("RavenDB")]
public class SessionHandoffTests : IClassFixture<RavenDBFixture>
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RavenDBFixture _fixture;

    public SessionHandoffTests(RavenDBFixture fixture) => _fixture = fixture;

    private static string NewSlug(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(string Slug, SessionTools Session, CampaignTools Tools)> NewCampaignAsync(string prefix)
    {
        var slug = NewSlug(prefix);
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        return (slug, TestCampaignToolsFactory.CreateTool<SessionTools>(_fixture), tools);
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        foreach (var doc in documents)
        {
            await session.StoreAsync(doc);
        }

        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10));
        await session.SaveChangesAsync();
    }

    private static Character Pc(string slug, string id, string name, string? locationId = null) => new()
    {
        Id = id,
        Name = name,
        IsPc = true,
        CampaignName = slug,
        MaxHp = 10,
        CurrentHp = 10,
        CurrentLocationId = locationId,
    };

    private static Character Npc(string slug, string id, string name) => new()
    {
        Id = id,
        Name = name,
        CampaignName = slug,
        MaxHp = 8,
        CurrentHp = 8,
    };

    private static SessionHandoff SampleHandoff(string npcId) => new()
    {
        StorySoFar = "The party came to the harbor after the lighthouse went dark and traced the missing lamp oil to the salt warehouse.",
        LastSession = "Questioned the harbormaster; found tar on the lighthouse stairs.",
        OpenThreads = ["Who holds the lamp key", "Blue lantern signal"],
        NpcsInPlay = [new NpcStance { Id = npcId, Stance = "nervous, hiding a debt" }],
        PartyIntent = "Stake out the warehouse at night tide.",
        Tone = "Low-magic harbor mystery.",
    };

    [Fact]
    public async Task EndSession_Handoff_RoundTripsIntoNextStartSession()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-roundtrip");
        var npcId = $"chars/{slug}-oda";
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"), Npc(slug, npcId, "Oda the Harbormaster"));

        var first = await session.StartSession(slug);
        Assert.True(first.Success, first.Summary);
        Assert.Null(first.Data!.Handoff);
        Assert.Null(first.Data.RecentDigest);

        var ended = await session.EndSession(slug, SampleHandoff(npcId));
        Assert.True(ended.Success, ended.Summary);

        var second = await session.StartSession(slug);
        Assert.True(second.Success, second.Summary);
        Assert.Equal(2, second.Data!.SessionNumber);
        var handoff = second.Data.Handoff;
        Assert.NotNull(handoff);
        Assert.Equal(1, handoff.FromSession);
        Assert.False(handoff.Checkpoint);
        Assert.Equal(SampleHandoff(npcId).StorySoFar, handoff.StorySoFar);
        Assert.Equal(SampleHandoff(npcId).LastSession, handoff.LastSession);
        Assert.Equal(["Who holds the lamp key", "Blue lantern signal"], handoff.OpenThreads);
        Assert.Equal(npcId, Assert.Single(handoff.NpcsInPlay!).Id);
        Assert.Equal("Stake out the warehouse at night tide.", handoff.PartyIntent);
        Assert.Null(second.Data.RecentDigest);
    }

    [Fact]
    public async Task EndSession_OverCap_RejectsWithOverage_AndLeavesSessionOpen()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-cap");
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"));
        Assert.True((await session.StartSession(slug)).Success);

        var tooLong = new SessionHandoff
        {
            StorySoFar = new string('s', SessionHandoffRules.StorySoFarMax + 100),
            LastSession = "Fine.",
            OpenThreads = Enumerable.Range(1, SessionHandoffRules.OpenThreadsMaxCount + 1).Select(i => $"thread {i}").ToList(),
        };
        var rejected = await session.EndSession(slug, tooLong);

        Assert.False(rejected.Success);
        Assert.Equal(ToolErrors.InvalidArgument, rejected.Error);
        Assert.Contains("storySoFar is 900 chars (max 800, 100 over)", rejected.Summary);
        Assert.Contains("openThreads has 7 items", rejected.Summary);
        Assert.Contains("nothing was saved", rejected.Summary);

        var resumed = await session.StartSession(slug);
        Assert.True(resumed.Data!.Resumed);
        Assert.Null(resumed.Data.Handoff);
    }

    [Fact]
    public async Task EndSession_MissingLastSession_IsRejected()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-required");
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"));
        Assert.True((await session.StartSession(slug)).Success);

        var rejected = await session.EndSession(slug, new SessionHandoff { StorySoFar = "Something." });

        Assert.False(rejected.Success);
        Assert.Contains("lastSession is required", rejected.Summary);
    }

    [Fact]
    public async Task EndSession_UnknownNpcId_RejectsWithCloseMatches()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-npc");
        var npcId = $"chars/{slug}-oda";
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"), Npc(slug, npcId, "Oda the Harbormaster"));
        Assert.True((await session.StartSession(slug)).Success);

        var handoff = SampleHandoff("chars/harbormaster");
        var rejected = await session.EndSession(slug, handoff);

        Assert.False(rejected.Success);
        Assert.Contains("'chars/harbormaster' is not a character in this campaign", rejected.Summary);
        Assert.Contains(npcId, rejected.Summary);
    }

    [Fact]
    public async Task Checkpoint_KeepsSessionOpen_AndResumedStartSessionReturnsIt()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-checkpoint");
        var npcId = $"chars/{slug}-oda";
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"), Npc(slug, npcId, "Oda"));
        Assert.True((await session.StartSession(slug)).Success);

        var checkpoint = await session.EndSession(slug, SampleHandoff(npcId), checkpoint: true);
        Assert.True(checkpoint.Success, checkpoint.Summary);

        var resumed = await session.StartSession(slug);
        Assert.True(resumed.Data!.Resumed);
        Assert.Equal(1, resumed.Data.SessionNumber);
        Assert.NotNull(resumed.Data.Handoff);
        Assert.True(resumed.Data.Handoff.Checkpoint);
        Assert.Equal(1, resumed.Data.Handoff.FromSession);

        // The final end_session overwrites the checkpoint and closes the session.
        var final = SampleHandoff(npcId);
        final.LastSession = "Relit the lamp.";
        Assert.True((await session.EndSession(slug, final)).Success);

        var next = await session.StartSession(slug);
        Assert.False(next.Data!.Resumed);
        Assert.False(next.Data.Handoff!.Checkpoint);
        Assert.Equal("Relit the lamp.", next.Data.Handoff.LastSession);
    }

    [Fact]
    public async Task RecapTextAlias_BecomesLastSession_AndKeepsPreviousStorySoFar()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-alias");
        var npcId = $"chars/{slug}-oda";
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"), Npc(slug, npcId, "Oda"));

        Assert.True((await session.StartSession(slug)).Success);
        Assert.True((await session.EndSession(slug, SampleHandoff(npcId))).Success);

        Assert.True((await session.StartSession(slug)).Success);
        var legacy = await session.EndSession(slug, recapText: "They found the lamp key under the bell.");
        Assert.True(legacy.Success, legacy.Summary);
        Assert.Contains("previous one was kept", legacy.Summary);

        var third = await session.StartSession(slug);
        Assert.Equal(2, third.Data!.Handoff!.FromSession);
        Assert.Equal("They found the lamp key under the bell.", third.Data.Handoff.LastSession);
        Assert.Equal(SampleHandoff(npcId).StorySoFar, third.Data.Handoff.StorySoFar);
    }

    [Fact]
    public async Task LegacySessionWithoutHandoff_FallsBackToCappedDigest()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-legacy");
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"));
        await StoreAsync(new SessionLog
        {
            Id = $"{slug}/state/sessions",
            CampaignName = slug,
            Sessions =
            [
                new SessionLog.SessionRecord { Number = 1, IsOpen = false, RecapText = "Legacy recap: the party burned the granary." },
            ],
        });

        var start = await session.StartSession(slug);

        Assert.True(start.Success, start.Summary);
        Assert.Equal(2, start.Data!.SessionNumber);
        Assert.Null(start.Data.Handoff);
        Assert.NotNull(start.Data.RecentDigest);
        Assert.Contains("Legacy recap: the party burned the granary.", start.Data.RecentDigest);
        Assert.True(start.Data.RecentDigest.Length <= CampaignVault.Data.SessionDigestBuilder.MaxChars);
        Assert.Contains("No handoff stored", start.Summary);
    }

    [Fact]
    public async Task StartSession_EngineFactsComeFromDb_EvenWhenHandoffContradictsThem()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-truth");
        var pcId = $"chars/{slug}-pc";
        var lighthouse = $"locations/{slug}-lighthouse";
        var pc = Pc(slug, pcId, "Tamsin", lighthouse);
        pc.CurrentHp = 4;
        pc.SystemStats = new Dnd5eExtension
        {
            ArmorClass = 14,
            Level = 3,
            StatusEffects = [new StatusEffect { Name = "Exhaustion 1", Category = "Condition" }],
        };
        pc.Needs.ActiveNeeds["hunger"] = 75f;
        await StoreAsync(pc,
            new Item { Id = $"items/{slug}-sword", Name = "Shortsword", HolderId = pcId, IsEquipped = true, CampaignName = slug },
            new Item { Id = $"items/{slug}-picks", Name = "Thieves' Tools", HolderId = pcId, CampaignName = slug });

        Assert.True((await session.StartSession(slug)).Success);
        var handoff = new SessionHandoff { LastSession = "The party rested, unhurt, at the Gull Tavern." };
        Assert.True((await session.EndSession(slug, handoff)).Success);

        var start = await session.StartSession(slug);

        Assert.True(start.Success, start.Summary);
        var member = Assert.Single(start.Data!.Party);
        Assert.Equal("4/10", member.Hp);
        Assert.Equal(lighthouse, member.LocationId);
        Assert.Equal(14, member.Ac);
        Assert.Equal(3, member.Level);
        Assert.Equal(["Exhaustion 1"], member.Conditions);
        Assert.Equal(["Shortsword"], member.Equipped);
        Assert.Equal(["Thieves' Tools"], member.Carried);
        Assert.Equal(75, member.HighNeeds!["hunger"]);
        Assert.False(member.HighNeeds.ContainsKey("thirst"));
        Assert.Contains($"fullDetailLocationId={lighthouse}", start.Summary);
        Assert.Equal($"{pcId}:4/10@{lighthouse}", start.Data.PartyFingerprint);
    }

    [Fact]
    public async Task StartSession_StaysUnderBudget_AndDoesNotGrowWithSessionsPlayed()
    {
        var (slug, session, _) = await NewCampaignAsync("handoff-budget");
        var npcIds = Enumerable.Range(1, 4).Select(i => $"chars/{slug}-npc{i}").ToList();
        var pcA = Pc(slug, $"chars/{slug}-pc-a", "Tamsin", $"locations/{slug}-harbor");
        var pcB = Pc(slug, $"chars/{slug}-pc-b", "Bram", $"locations/{slug}-harbor");
        await StoreAsync([pcA, pcB, .. npcIds.Select((id, i) => (object)Npc(slug, id, $"Npc {i}"))]);

        var sizes = new List<int>();
        for (var sessionNumber = 1; sessionNumber <= 4; sessionNumber++)
        {
            var start = await session.StartSession(slug);
            Assert.True(start.Success, start.Summary);
            sizes.Add(WireLength(start));

            // Memories pile up every session — the old start_session shipped all of them in full.
            await AddMemoriesAsync(pcA.Id, sessionNumber, count: 5);
            await AddMemoriesAsync(pcB.Id, sessionNumber, count: 5);

            var handoff = SampleHandoff(npcIds[0]);
            handoff.NpcsInPlay = npcIds.Select(id => new NpcStance { Id = id, Stance = "watching the party closely" }).ToList();
            handoff.LastSession = $"Session {sessionNumber}: " + new string('x', 400);
            Assert.True((await session.EndSession(slug, handoff)).Success);
        }

        var last = await session.StartSession(slug);
        sizes.Add(WireLength(last));

        Assert.All(sizes, size => Assert.True(size <= 3500, $"start_session was {size} chars: [{string.Join(", ", sizes)}]"));
        // Sessions 2..5 all carry a same-sized handoff; only digits in ids/counters may differ.
        Assert.True(sizes[1..].Max() - sizes[1..].Min() < 100, $"start_session grew across sessions: [{string.Join(", ", sizes)}]");
        Assert.Equal(20, last.Data!.Party.Single(p => p.Id == pcA.Id).MemoryCount);
        Assert.Equal(PartySessionView.KeyMemoryCount, last.Data.Party.Single(p => p.Id == pcA.Id).KeyMemories!.Count);
    }

    [Fact]
    public async Task FirstTakeTurnAfterStartSession_IsFull_AndKickoffFingerprintIsNotDrift()
    {
        var (slug, session, tools) = await NewCampaignAsync("handoff-cursor");
        await StoreAsync(Pc(slug, $"chars/{slug}-pc", "Tamsin"));

        Assert.True((await session.StartSession(slug)).Success);
        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var delta = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true, ClientPartyFingerprint = seed.Data.PartyFingerprint }, slug);
        Assert.Equal(TurnMode.Delta, delta.Data!.Mode);

        // New conversation: start_session again (resumes), then the first take_turn must be a full reseed.
        var kickoff = await session.StartSession(slug);
        var first = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true, ClientPartyFingerprint = kickoff.Data!.PartyFingerprint }, slug);

        Assert.True(first.Success, first.Summary);
        Assert.Equal(TurnMode.Full, first.Data!.Mode);
        Assert.DoesNotContain("didn't match", JsonSerializer.Serialize(first, WireOptions));

        var second = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true, ClientPartyFingerprint = first.Data.PartyFingerprint }, slug);
        Assert.Equal(TurnMode.Delta, second.Data!.Mode);
    }

    [Fact]
    public async Task RolledBackTakeTurn_DoesNotReportEventsAsLogged()
    {
        var (slug, _, tools) = await NewCampaignAsync("rollback-summary");
        var pcId = $"chars/{slug}-pc";
        await StoreAsync(Pc(slug, pcId, "Tamsin"));

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Narrative = "Tamsin spots tar on the stairs.",
            Changes =
            [
                new EventOccurred { Summary = "Tamsin spots tar on the stairs.", Involved = [pcId] },
                // Witnessed without sourceEventIds fails validation and rolls back the batch.
                new KnowledgeUpdate { CharacterId = pcId, Topic = "tar", Details = "Fresh tar.", Source = MemorySource.Witnessed },
            ],
        }, slug);

        Assert.False(result.Success);
        Assert.Contains("NO CHANGES WERE SAVED", result.Summary);
        Assert.DoesNotContain("Event logged", result.Summary);
        Assert.Contains("rolled back with the batch", result.Summary);
    }

    private async Task AddMemoriesAsync(string characterId, int sessionNumber, int count)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var character = await session.LoadAsync<Character>(characterId);
        for (var i = 0; i < count; i++)
        {
            var topic = $"clue {sessionNumber}.{i}";
            character.Psychology.Memories[topic] = new MemoryNode
            {
                Topic = topic,
                Details = "Learned about the smugglers moving oil at night past the harbor chain. " + new string('d', 300),
                DayAcquired = sessionNumber,
                Salience = 0.1 * i,
            };
        }

        await session.SaveChangesAsync();
    }

    /// <summary>Wire size of the start_session payload, excluding worldPressure: pressure is capped by its
    /// own orchestrator and is not what the handoff trims (these bare fixtures also trip integrity warnings
    /// that a seeded campaign wouldn't).</summary>
    private static int WireLength<T>(ToolResult<T> toolResult)
    {
        var pressure = toolResult.WorldPressure;
        toolResult.WorldPressure = null;
        var structured = JsonSerializer.SerializeToElement(toolResult, WireOptions);
        toolResult.WorldPressure = pressure;
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "" }],
            StructuredContent = structured,
        };
        McpResponseCleaner.Apply(result);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text.Length;
    }
}
