using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>IMPRESSIONS_PLAN.md I5/I6 through take_turn: how a beat lands on each observer, and what is kept.</summary>
[Collection("RavenDB")]
public class ImpressionScenarioTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public ImpressionScenarioTests(RavenDBFixture fixture) => _fixture = fixture;

    private sealed record Green(string Slug, CampaignTools Tools, string LocId, string PcId, string PipId, string TamId, string WennaId, string CaptainId);

    /// <summary>A village green by day: a child, an adult, a superstitious elder and a sharp-eyed guard captain.</summary>
    private async Task<Green> SeedGreenAsync()
    {
        var slug = "imp-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var g = new Green(slug, tools, $"locations/{slug}-green", $"chars/{slug}-maeve", $"chars/{slug}-pip", $"chars/{slug}-tam",
            $"chars/{slug}-wenna", $"chars/{slug}-captain");
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = g.LocId, Name = "Village Green", Description = "Grass, a well, a bench.", Type = LocationType.Settlement
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = g.PcId, Name = "Maeve", IsPc = true, CurrentLocationId = g.LocId, MaxHp = 18, CurrentHp = 18,
                SystemStats = new Dnd5eExtension { ArmorClass = 12, Charisma = 10 }
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = g.PipId, Name = "Pip", CurrentLocationId = g.LocId, MaxHp = 4, CurrentHp = 4, LifeStage = LifeStage.Child
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = g.TamId, Name = "Tam", CurrentLocationId = g.LocId, MaxHp = 9, CurrentHp = 9, LifeStage = LifeStage.Adult
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = g.WennaId, Name = "Old Wenna", CurrentLocationId = g.LocId, MaxHp = 6, CurrentHp = 6, LifeStage = LifeStage.Elder,
                Psychology = new PsychologyProfile { Traits = ["superstitious", "sharp-tongued"] }
            });
            var keen = new Dnd5eExtension { Wisdom = 16 };
            keen.SkillModifiers["Insight"] = 7;
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = g.CaptainId, Name = "Captain Ros", CurrentLocationId = g.LocId, MaxHp = 30, CurrentHp = 30, LifeStage = LifeStage.Adult,
                SystemStats = keen
            });
            await session.SaveChangesAsync();
        }

        var lit = await tools.TakeTurn(new TakeTurnRequest
        {
            FullDetailLocationId = g.LocId, Changes = [new LocationUpdate { LocationId = g.LocId, Lighting = "bright" }],
            Narrative = "Midday on the green."
        }, slug);
        Assert.True(lit.Success, lit.Summary);
        return g;
    }

    private async Task<Character> LoadAsync(string id)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        return (await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken))!;
    }

    private async Task SetAsync(string id, Action<Character> change)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        var c = (await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken))!;
        change(c);
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Impressions(ToolResult<TurnResult> result) =>
        Assert.Single(result.Data!.Summary ?? [], l => l.StartsWith("IMPRESSIONS"));

    private static string EntryFor(string line, string name) =>
        line.Split(" | ").Select(e => e.Trim()).First(e => e.StartsWith(name + ":") || e.Contains("): " + name + ":"));

    private static Task<ToolResult<TurnResult>> CastAsync(CampaignTools tools, string slug, string pcId, Dictionary<string, string> parameters,
        List<string>? targets = null, int? sleight = null)
    {
        var changes = new List<WorldChange>();
        if (sleight is { } bonus)
        {
            // F3: the caster palms the gesture; observers' passive Perception must meet the total to catch it.
            changes.Add(new RulesetAction
            {
                CharacterId = pcId, ActionType = RulesetActionType.SkillCheck, ActionName = "Sleight of Hand",
                Parameters = new Dictionary<string, string> { ["skill"] = "Sleight of Hand", ["dc"] = "10", ["bonus"] = bonus.ToString() }
            });
        }

        changes.Add(new RulesetAction
        {
            CharacterId = pcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "minor_illusion",
            TargetIds = targets ?? [],
            Parameters = new Dictionary<string, string>(parameters) { ["resolution"] = "utility", ["innate"] = "true" }
        });
        return tools.TakeTurn(new TakeTurnRequest { Changes = [.. changes], Narrative = "Maeve weaves an illusion." }, slug);
    }

    [Fact]
    public async Task Butterflies_enchant_the_child_please_the_adult_and_unsettle_the_superstitious_elder()
    {
        var g = await SeedGreenAsync();

        var cast = await CastAsync(g.Tools, g.Slug, g.PcId,
            new() { ["mode"] = "image", ["appeal"] = "delight", ["subject"] = "butterflies" }, targets: [g.PipId]);

        Assert.True(cast.Success, cast.Summary);
        var line = Impressions(cast);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Assert.StartsWith("IMPRESSIONS (minor illusion by Maeve, delight):", line);
        Assert.Contains("a child", EntryFor(line, "Pip"));
        Assert.Contains("uneasy", EntryFor(line, "Old Wenna"));
        Assert.Contains("distrusts magic", EntryFor(line, "Old Wenna"));
        Assert.DoesNotContain(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses") && l.Contains("Pip"));

        var pip = await LoadAsync(g.PipId);
        var wenna = await LoadAsync(g.WennaId);
        var tam = await LoadAsync(g.TamId);
        Assert.Equal("delight", Assert.Single(pip.Impressions).Tone);
        Assert.Equal("delighted", pip.Psychology.CurrentMood);

        // F1: the show was for Pip, so only Pip's opinion is the engine's to move. The bystanders' are proposed.
        Assert.True(ImpressionOpinion(pip, g.PcId) > 0);
        Assert.Equal(0, ImpressionOpinion(tam, g.PcId));
        Assert.Equal(0, ImpressionOpinion(wenna, g.PcId));
        Assert.Contains("toward Maeve, unrecorded", EntryFor(line, "Tam"));
        Assert.Contains("toward Maeve, unrecorded", EntryFor(line, "Old Wenna"));
        Assert.Contains("commit a relationship if it fits", line);
        Assert.Equal("delight", Assert.Single(tam.Impressions).Tone); // the impression itself is kept either way
    }

    private static int ImpressionOpinion(Character c, string actorId) =>
        c.Social.Relationships.FirstOrDefault(kv => string.Equals(kv.Key, actorId, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public async Task A_bouquet_reaches_only_the_one_it_is_handed_to()
    {
        var g = await SeedGreenAsync();

        var gift = await g.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new EventOccurred
                {
                    Summary = "Maeve hands Tam a bouquet of wildflowers", Category = EventCategory.Conversation,
                    Involved = [g.PcId, g.TamId], InitiatorId = g.PcId, Appeal = "affection", AppealSubject = "wildflowers"
                }
            ],
            Narrative = "Maeve hands Tam the flowers she picked on the road."
        }, g.Slug);

        Assert.True(gift.Success, gift.Summary);
        var line = Impressions(gift);
        Assert.Contains("Tam: touched", line);
        Assert.DoesNotContain("Pip", line);
        Assert.Equal(2, ImpressionOpinion(await LoadAsync(g.TamId), g.PcId));
        Assert.Empty((await LoadAsync(g.PipId)).Impressions);
    }

    [Fact]
    public async Task Insincere_flattery_wins_the_dull_and_insults_the_sharp()
    {
        var g = await SeedGreenAsync();

        var flatter = await g.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new EventOccurred
                {
                    Summary = "Maeve praises the captain's leadership", Category = EventCategory.Conversation,
                    Involved = [g.PcId, g.CaptainId, g.TamId], InitiatorId = g.PcId, Appeal = "pride", Deceptive = true
                }
            ],
            Narrative = "Maeve lays the praise on thick."
        }, g.Slug);

        Assert.True(flatter.Success, flatter.Summary);
        var line = Impressions(flatter);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Assert.Contains("insulted", EntryFor(line, "Captain Ros"));
        Assert.Contains("saw through it: Insight 17 vs 11 (no roll: the liar's average)", EntryFor(line, "Captain Ros"));
        Assert.Contains("flattered", EntryFor(line, "Tam"));
        Assert.True(ImpressionOpinion(await LoadAsync(g.CaptainId), g.PcId) < 0);
        Assert.True(ImpressionOpinion(await LoadAsync(g.TamId), g.PcId) > 0);
    }

    [Fact]
    public async Task A_bad_tone_fails_the_commit()
    {
        var g = await SeedGreenAsync();

        var cast = await CastAsync(g.Tools, g.Slug, g.PcId, new() { ["mode"] = "image", ["appeal"] = "glee" });

        Assert.False(cast.Success);
        Assert.Contains("isn't a tone", string.Join("\n", cast.Data?.Summary ?? []) + cast.Summary);
    }

    // ---- the playtest: the ghost and the lone scout on the night road ----

    private static Task<ToolResult<TurnResult>> GhostAsync(NightRoadSightTests.Road r, int? sleight = 40) =>
        CastAsync(r.Tools, r.Slug, r.PcId, new()
        {
            ["mode"] = "image", ["appeal"] = "fear", ["intensity"] = "strong", ["deceptive"] = "true", ["subject"] = "a ghostly goblin",
            ["dc"] = "13"
        }, targets: [r.GoblinId], sleight: sleight);

    [Fact]
    public async Task The_lone_scout_flees_the_ghost_and_the_fourth_fright_is_weaker_than_the_first()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        await SetAsync(r.GoblinId, c =>
        {
            c.DistinctiveFeatures = ["Darkvision 60 ft."];
            c.SystemStats.Morale = 25;
        });

        var frights = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var cast = await GhostAsync(r);
            Assert.True(cast.Success, cast.Summary);
            frights.Add(EntryFor(Impressions(cast), "Goblin Scout"));
            TestContext.Current.TestOutputHelper?.WriteLine(frights[^1]);
        }

        Assert.Contains("→ flees", frights[0]);
        Assert.Contains("believes it; examining it takes an action: Investigation vs DC 13", frights[0]);
        Assert.Contains("seen it 3 times", frights[3]);
        Assert.DoesNotContain("→ flees", frights[3]);
        Assert.DoesNotContain("Corren", string.Join(" ", frights));

        var goblin = await LoadAsync(r.GoblinId);
        Assert.Equal(4, goblin.Impressions.Count);
        Assert.Equal(0, ImpressionOpinion(goblin, r.PcId)); // D4: she palmed the gesture, so he thinks it's a ghost, not Maeve
    }

    [Fact]
    public async Task Examining_the_illusion_and_meeting_the_dc_sees_through_it()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        await SetAsync(r.GoblinId, c => c.DistinctiveFeatures = ["Darkvision 60 ft."]);
        Assert.True((await GhostAsync(r)).Success);

        var look = await r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = r.GoblinId, ActionType = RulesetActionType.SkillCheck, ActionName = "Investigation",
                    Parameters = new Dictionary<string, string> { ["skill"] = "Investigation", ["dc"] = "13", ["bonus"] = "30", ["disbelieve"] = r.PcId }
                }
            ],
            Narrative = "The scout squints hard at the pale shape."
        }, r.Slug);

        Assert.True(look.Success, look.Summary);
        Assert.Contains(look.Data!.Summary ?? [], l => l.StartsWith("DISBELIEVE: Goblin Scout sees through the minor illusion"));
        var seen = (await LoadAsync(r.GoblinId)).Impressions[0];
        Assert.Equal(ImpressionRead.SawThrough, seen.Read);
        Assert.Equal("scorn", seen.Tone);
    }

    // ---- F1–F4 ----

    [Fact]
    public async Task A_scout_who_watches_Maeve_cast_suspects_her_and_thinks_less_of_her()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        await SetAsync(r.GoblinId, c => c.DistinctiveFeatures = ["Darkvision 60 ft."]);

        var cast = await GhostAsync(r, sleight: null);

        Assert.True(cast.Success, cast.Summary);
        var scout = EntryFor(Impressions(cast), "Goblin Scout");
        Assert.Contains("saw Maeve cast it: connects it to Maeve; examining it: Investigation vs DC 13", scout);
        var goblin = await LoadAsync(r.GoblinId);
        Assert.Equal(ImpressionRead.Suspects, goblin.Impressions[0].Read);
        Assert.True(ImpressionOpinion(goblin, r.PcId) < 0); // addressed, and now he knows whose horror it is
    }

    [Fact]
    public async Task A_palmed_gesture_is_caught_only_by_a_passive_perception_that_meets_it()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        await SetAsync(r.GoblinId, c =>
        {
            c.DistinctiveFeatures = ["Darkvision 60 ft."];
            c.SystemStats.Attributes["passivePerception"] = 25;
        });

        var cast = await GhostAsync(r, sleight: 0);

        Assert.True(cast.Success, cast.Summary);
        Assert.Contains("caught Maeve's gesture (Perception 25 vs Sleight of Hand", EntryFor(Impressions(cast), "Goblin Scout"));
    }

    [Fact]
    public async Task In_a_fight_the_lone_scout_is_alone_and_one_with_a_comrade_is_not()
    {
        // In a fight, palming the gesture would cost the action the spell needs (5e), so Maeve casts openly here.
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: true);
        await SetAsync(r.GoblinId, c => c.DistinctiveFeatures = ["Darkvision 60 ft."]);
        var alone = await GhostAsync(r, sleight: null);
        Assert.True(alone.Success, alone.Summary);
        Assert.Contains("alone", EntryFor(Impressions(alone), "Goblin Scout"));

        var r2 = await NightRoadSightTests.SeedRoadAsync(_fixture, startCombat: false);
        await SetAsync(r2.GoblinId, c => c.DistinctiveFeatures = ["Darkvision 60 ft."]);
        var mateId = $"chars/{r2.Slug}-mate";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character
            {
                Id = mateId, Name = "Goblin Mate", CampaignName = r2.Slug, CurrentLocationId = r2.LocId, MaxHp = 7, CurrentHp = 7,
                SystemStats = new Dnd5eExtension { ArmorClass = 15 }
            }, TestContext.Current.CancellationToken);
            session.Advanced.WaitForIndexesAfterSaveChanges(timeout: TimeSpan.FromSeconds(10), throwOnTimeout: true);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var started = await r2.Tools.StartCombat(r2.LocId, [r2.PcId, r2.GoblinId, mateId], r2.Slug);
        Assert.True(started.Success, started.Summary);
        await NightRoadSightTests.ToMaevesTurnAsync(r2, started.Data!.ActiveTurnId);
        var together = await GhostAsync(r2, sleight: null);
        Assert.True(together.Success, together.Summary);
        Assert.Contains("among allies", EntryFor(Impressions(together), "Goblin Scout"));
    }

    [Fact]
    public async Task Light_the_engine_assumed_and_that_hid_someone_is_named_for_correction()
    {
        var r = await NightRoadSightTests.SeedRoadAsync(_fixture, lighting: "auto", startCombat: false);
        Assert.True((await r.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new ActivityChange { CharacterId = r.IlsaId, Departing = true, NewActivity = "walking off" }],
            Narrative = "Ilsa walks off down the road."
        }, r.Slug)).Success);

        var plain = await CastAsync(r.Tools, r.Slug, r.PcId, new() { ["mode"] = "image" });
        Assert.True(plain.Success, plain.Summary);
        Assert.Contains(plain.Data!.Context ?? [], l => l.StartsWith("Light at North Road taken as dim (Wilderness, 06:00)"));

        var bright = await NightRoadSightTests.SeedRoadAsync(_fixture, lighting: "dim", startCombat: false);
        Assert.True((await bright.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new ActivityChange { CharacterId = bright.IlsaId, Departing = true, NewActivity = "walking off" }],
            Narrative = "Ilsa walks off down the road."
        }, bright.Slug)).Success);
        var set = await CastAsync(bright.Tools, bright.Slug, bright.PcId, new() { ["mode"] = "image", ["appeal"] = "awe" });
        Assert.DoesNotContain("Light at", Impressions(set)); // set by the narrator: nothing to correct
        Assert.DoesNotContain(set.Data!.Context ?? [], l => l.StartsWith("Light at"));
    }
}
