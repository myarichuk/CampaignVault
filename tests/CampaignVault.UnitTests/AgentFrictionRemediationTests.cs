using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Models.Converters;
using CampaignVault.Schema;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Pins the fixes from the Grok playthrough diagnosis: partial upserts preserve what they omit, mid-play
/// creation is a take_turn change, exits come back reciprocal, common field slips are aliased and every
/// unknown field is reported at once, the calendar is editable, and plot clues accept bare strings.
/// </summary>
[Collection("RavenDB")]
public class AgentFrictionRemediationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public AgentFrictionRemediationTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    // ---------------------------------------------------------------- Phase 1: preserve-when-omitted

    [Fact]
    public async Task CharacterUpsert_OmittedScalars_PreserveStoredValues()
    {
        var repo = _fixture.CreateRepository();
        var id = NewId("chars/preserve");
        var locId = NewId("locations/preserve");

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertLocationAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                new LocationUpsertRequest { Id = locId, Name = "Study", Description = "Books." });
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                new CharacterUpsertRequest
                {
                    Id = id, Name = "Khelben", IsPc = true, KeepAlive = true, CurrentHp = 20, MaxHp = 20,
                    CurrentLocationId = locId, CurrentActivity = "reading", Notes = "archmage", LifeStage = LifeStage.Adult,
                });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            // The partial update Grok sent: id plus one field. Everything else must survive.
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                new CharacterUpsertRequest { Id = id, CurrentAppearance = "ink-stained fingers" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            Assert.Equal("Khelben", stored.Name);
            Assert.True(stored.IsPc);
            Assert.True(stored.KeepAlive);
            Assert.Equal(20, stored.CurrentHp);
            Assert.Equal(20, stored.MaxHp);
            Assert.Equal(locId, stored.CurrentLocationId);
            Assert.Equal("reading", stored.CurrentActivity);
            Assert.Equal("archmage", stored.Notes);
            Assert.Equal(LifeStage.Adult, stored.LifeStage);
            Assert.Equal("ink-stained fingers", stored.CurrentAppearance);
        }
    }

    [Fact]
    public async Task CharacterUpsert_Create_RequiresName()
    {
        var repo = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => repo.UpsertCharacterAsync(
            _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
            new CharacterUpsertRequest { Id = NewId("chars/nameless") }));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocationAndItemUpsert_OmittedScalars_PreserveStoredValues()
    {
        var repo = _fixture.CreateRepository();
        var locId = NewId("locations/keep");
        var charId = NewId("chars/keep");
        var itemId = NewId("items/keep");

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = locId, Name = "Corridor", Description = "Long and dim.", Type = LocationType.Room,
                AmbientCrowd = "two guards", CurrentState = "torches lit",
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = charId, Name = "Holder" });
            await repo.UpsertItemAsync(cs, new ItemUpsertRequest
            {
                Id = itemId, Name = "Coin purse", Description = "Heavy.", HolderId = charId, Quantity = 3,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, DangerModifier = 5 });
            await repo.UpsertItemAsync(cs, new ItemUpsertRequest { Id = itemId, Tags = ["jingling"] });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var loc = await session.LoadAsync<Location>(locId, TestContext.Current.CancellationToken);
            Assert.Equal("Corridor", loc!.Name);
            Assert.Equal("Long and dim.", loc.Description);
            Assert.Equal(LocationType.Room, loc.Type);
            Assert.Equal("two guards", loc.AmbientCrowd);
            Assert.Equal("torches lit", loc.CurrentState);
            Assert.Equal(5, loc.DangerModifier);

            var item = await session.LoadAsync<Item>(itemId, TestContext.Current.CancellationToken);
            Assert.Equal("Coin purse", item!.Name);
            Assert.Equal("Heavy.", item.Description);
            Assert.Equal(charId, item.HolderId);
            Assert.Equal(3, item.Quantity);
            Assert.Contains("jingling", item.Tags);
        }
    }

    // ---------------------------------------------------------------- Phase 2: in-play creation verbs

    [Fact]
    public void CreateVerbs_AreRegisteredCommitTypes()
    {
        var parsed = Parse("""
            [
              {"$type":"character_create","characterId":"chars/x-khelben","name":"Khelben","currentLocationId":"locations/x","lifeStage":"adult"},
              {"$type":"item_create","itemId":"items/x-bell","name":"Whiskers' bell","holderId":"chars/x-khelben"},
              {"$type":"rumor_create","rumorId":"rumors/x","subject":"Bells","text":"A cat wears a silver bell."}
            ]
            """);

        Assert.IsType<CharacterCreate>(parsed[0]);
        Assert.IsType<ItemCreate>(parsed[1]);
        Assert.IsType<RumorCreate>(parsed[2]);
        Assert.Contains(CommitFieldReference.Authorable, v => v.Discriminator == "character_create");
        Assert.Contains(CommitFieldReference.Authorable, v => v.Discriminator == "item_create");
    }

    [Fact]
    public async Task ItemCreate_CreatesHeldItem_AndRefusesAnExistingId()
    {
        var repo = _fixture.CreateRepository();
        var charId = NewId("chars/giver");
        var itemId = NewId("items/gift");

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await repo.UpsertCharacterAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                new CharacterUpsertRequest { Id = charId, Name = "Giver" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var result = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
            [
                new ItemCreate { ItemId = itemId, Name = "Silver bell", HolderId = charId, Quantity = 1, Tags = ["gift"], MaxCharges = 3 },
            ]);
            Assert.True(result.Success, string.Join("\n", result.Summary));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var item = await session.LoadAsync<Item>(itemId, TestContext.Current.CancellationToken);
            Assert.NotNull(item);
            Assert.Equal(charId, item.HolderId);
            Assert.Equal("Silver bell", item.Name);
            Assert.Equal(3, item.CurrentCharges);
            Assert.Equal(TestCampaignDefaults.Slug, item.CampaignName);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var dup = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                [new ItemCreate { ItemId = itemId, Name = "Silver bell", HolderId = charId }]);
            Assert.False(dup.Success);
            Assert.Contains(dup.Summary, line => line.Contains("already exists") && line.Contains("\"$type\":\"item\""));

            var orphan = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                [new ItemCreate { ItemId = NewId("items/orphan"), Name = "Nothing", HolderId = "chars/nobody-here" }]);
            Assert.False(orphan.Success);
            Assert.Contains(orphan.Summary, line => line.Contains("does not exist"));
        }
    }

    [Fact]
    public void PlayProfile_NoLongerServesWorldBuild()
    {
        Assert.DoesNotContain("world_build", ToolProfiles.PlayTools);
        Assert.Contains("world_build", ToolProfiles.BuildTools);
        var message = ToolProfiles.UnknownToolMessage("world_build", ToolProfiles.PlayTools.ToList());
        Assert.Contains("on /play; it is on /build", message);
    }

    // ---------------------------------------------------------------- Phase 3: reciprocal exits

    [Fact]
    public async Task LocationUpsert_AddsTheReturnExit_UnlessOneWay()
    {
        var repo = _fixture.CreateRepository();
        var a = NewId("locations/hall");
        var b = NewId("locations/study");
        var chute = NewId("locations/cellar");

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = a, Name = "Hall", Description = "." });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = chute, Name = "Cellar", Description = "." });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = b, Name = "Study", Description = ".",
                Exits =
                [
                    new LocationExit(a, "Door to the hall", TravelCostHours: 0.1, Terrain: "indoor"),
                    new LocationExit(chute, "Laundry chute", OneWay: true),
                ],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var hall = await session.LoadAsync<Location>(a, TestContext.Current.CancellationToken);
            var back = Assert.Single(hall!.Exits, e => e.TargetLocationId == b);
            Assert.Equal(0.1, back.TravelCostHours);
            Assert.Equal("indoor", back.Terrain);

            var cellar = await session.LoadAsync<Location>(chute, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(cellar!.Exits, e => e.TargetLocationId == b);
        }
    }

    [Fact]
    public async Task WorldBuild_LinksBack_WhenTheTargetComesLaterInTheBatch()
    {
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture);
        var slug = "wb-exits-" + Guid.NewGuid().ToString("N")[..8];
        var first = $"locations/{slug}-first";
        var second = $"locations/{slug}-second";

        var result = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Locations =
            [
                new LocationUpsertRequest { Id = first, Name = "First", Description = ".", Exits = [new LocationExit(second, "Onward")] },
                new LocationUpsertRequest { Id = second, Name = "Second", Description = "." },
            ],
        }, slug);
        Assert.True(result.Success, result.Summary);

        using var session = _fixture.Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Location>(second, TestContext.Current.CancellationToken);
        Assert.Contains(stored!.Exits, e => e.TargetLocationId == first);
        Assert.Contains(result.Data!.Warnings, w => w.Contains("return exit"));
    }

    [Fact]
    public void AutoRepairLocationConnectivity_DefaultsOn() => Assert.True(new CampaignConfig().AutoRepairLocationConnectivity);

    // ---------------------------------------------------------------- Phase 4: aliases, clear, sweep

    [Fact]
    public void FieldAliases_RenameTheSlipsGrokMade()
    {
        var parsed = Parse("""
            [
              {"$type":"status_remove","characterId":"chars/a","statusName":"Prone"},
              {"$type":"knowledge_update","characterId":"chars/a","topic":"Bells","content":"Whiskers wears a bell now."},
              {"$type":"item_transfer","itemId":"items/bell","toHolderId":"chars/b"},
              {"$type":"engagement_relation","characterId":"chars/a","targetId":"chars/b","end":true},
              {"$type":"event","category":"Conversation","summary":"They talked.","participants":["chars/a","chars/b"]},
              {"$type":"item","itemId":"items/purse","holderId":"chars/b"}
            ]
            """);

        Assert.Equal("Prone", Assert.IsType<StatusRemove>(parsed[0]).Status);
        Assert.Equal("Whiskers wears a bell now.", Assert.IsType<KnowledgeUpdate>(parsed[1]).Details);
        Assert.Equal("chars/b", Assert.IsType<ItemTransfer>(parsed[2]).ToHolderId);
        Assert.True(Assert.IsType<EngagementRelationChange>(parsed[3]).Clear);
        Assert.Equal(["chars/a", "chars/b"], Assert.IsType<EventOccurred>(parsed[4]).Involved);
        Assert.Equal("chars/b", Assert.IsType<ItemTransfer>(parsed[5]).ToHolderId);
    }

    [Fact]
    public void FieldAlias_IsDropped_WhenTheRealFieldIsAlsoPresent()
    {
        var parsed = Parse("""[{"$type":"status_remove","characterId":"chars/a","status":"Prone","statusName":"Stunned"}]""");
        Assert.Equal("Prone", Assert.IsType<StatusRemove>(parsed[0]).Status);
    }

    [Fact]
    public void UnknownFields_AreReportedForTheWholeBatch_InOneError()
    {
        var changes = JsonSerializer.Deserialize<JsonElement>("""
            [
              {"$type":"hp","characterId":"chars/a","delta":-3,"targt":"chars/b"},
              {"$type":"event","category":"Arrival","summry":"x","involved":[]},
              {"$type":"travel","characterId":"chars/a","destinationLocationId":"locations/l","mood":"grim"}
            ]
            """);

        Assert.False(CommitChangesParser.TryParse(changes, out _, out var error));
        Assert.NotNull(error);
        Assert.StartsWith(CommitBindingError.UnknownFieldsMarker, error);
        Assert.Contains("Unknown field 'targt' at $[0] ($type hp)", error);
        Assert.Contains("Unknown field 'summry' at $[1] ($type event). Did you mean 'summary'", error);
        Assert.Contains("Unknown field 'mood' at $[2] ($type travel)", error);
        Assert.Contains("Nothing was applied", error);
        Assert.Contains("Fields of hp:", error);
        Assert.True(error.Length <= CommitBindingError.UnknownFieldsMaxChars, error);
    }

    [Fact]
    public async Task EngagementRelation_ClearFlag_EndsTheRelation()
    {
        var repo = _fixture.CreateRepository();
        var a = NewId("chars/petter");
        var b = NewId("chars/cat");

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = a, Name = "Petter" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = b, Name = "Cat" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var set = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                [new EngagementRelationChange { CharacterId = a, TargetId = b, Verb = "petting", Category = EngagementCategory.Physical }]);
            Assert.True(set.Success, string.Join("\n", set.Summary));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cleared = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, TestCampaignDefaults.Slug),
                [new EngagementRelationChange { CharacterId = a, TargetId = b, Verb = "petting", Clear = true }]);
            Assert.True(cleared.Success, string.Join("\n", cleared.Summary));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var petter = await session.LoadAsync<Character>(a, TestContext.Current.CancellationToken);
            var cat = await session.LoadAsync<Character>(b, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(petter!.SystemStats.EngagementRelations ?? [], r => r.TargetId == b);
            Assert.DoesNotContain(cat!.SystemStats.EngagementRelations ?? [], r => r.TargetId == a);
        }
    }

    // ---------------------------------------------------------------- Phase 5: calendar on campaign_update

    [Fact]
    public async Task CampaignUpdate_SetsTheCalendar_WithoutMovingElapsedDays()
    {
        var tools = TestCampaignToolsFactory.Create(_fixture);
        var slug = "clock-" + Guid.NewGuid().ToString("N")[..8];
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var repo = _fixture.CreateRepository();

        int elapsedBefore;
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            elapsedBefore = (await repo.GetTimeAsync(_fixture.CreateCampaignSession(session, slug))).TotalDaysElapsed;
            var result = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, slug),
                [new CampaignUpdateChange { LoreYear = 1372, LoreMonth = 7, LoreDay = 14, Hour = 21, LoreEpoch = "DR" }]);
            Assert.True(result.Success, string.Join("\n", result.Summary));
            Assert.Contains(result.Summary, line => line.Contains("Calendar set"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var time = await repo.GetTimeAsync(_fixture.CreateCampaignSession(session, slug));
            Assert.Equal(1372, time.Year);
            Assert.Equal(7, time.Month);
            Assert.Equal(14, time.Day);
            Assert.Equal(21, time.Hour);
            Assert.Equal("DR", time.Epoch);
            Assert.Equal(elapsedBefore, time.TotalDaysElapsed);

            var bad = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, slug),
                [new CampaignUpdateChange { LoreMonth = 13 }]);
            Assert.False(bad.Success);
            Assert.Contains(bad.Summary, line => line.Contains("loreMonth"));
        }
    }

    // ---------------------------------------------------------------- Phase 6: bare-string clues

    [Fact]
    public void PlotClues_AcceptBareStrings_AndRoundTripObjects()
    {
        var clues = JsonSerializer.Deserialize<List<PlotClue>>("""
            ["A bloody glove by the door", {"id":"ledger","description":"The ledger is missing a page","involvedEntityIds":["chars/clerk"]}]
            """)!;

        Assert.Equal(2, clues.Count);
        Assert.Equal("a-bloody-glove-by-the-door", clues[0].Id);
        Assert.Equal("A bloody glove by the door", clues[0].Description);
        Assert.Equal("ledger", clues[1].Id);
        Assert.Equal(["chars/clerk"], clues[1].InvolvedEntityIds);

        var json = JsonSerializer.Serialize(clues[1]);
        Assert.Contains("\"id\":\"ledger\"", json);
        Assert.Contains("\"description\":\"The ledger is missing a page\"", json);
        var again = JsonSerializer.Deserialize<PlotClue>(json)!;
        Assert.Equal(clues[1], again with { InvolvedEntityIds = clues[1].InvolvedEntityIds });
    }

    private static WorldChange[] Parse(string json)
    {
        var ok = CommitChangesParser.TryParse(JsonSerializer.Deserialize<JsonElement>(json), out var parsed, out var error);
        Assert.True(ok, error);
        return parsed!;
    }
}
