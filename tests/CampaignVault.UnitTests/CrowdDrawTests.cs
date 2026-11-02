using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Context;
using CampaignVault.Services;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// WITNESS_CHANNEL_PLAN.md S1–S2: a beat that pulls attention (a song, a shouting match, loud haggling) gets an engine-rolled
/// chance that someone from the crowd comes over, tilted by the LLM's crowdModifier and limited by a per-location cooldown.
/// </summary>
[Collection("RavenDB")]
public class CrowdDrawTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CrowdDrawTests(RavenDBFixture fixture) => _fixture = fixture;

    // ---- S1: which beats pull a crowd ----

    private static RulesetAction Perform(params (string Key, string Value)[] extra)
    {
        var action = new RulesetAction
        {
            CharacterId = "chars/bard", ActionName = "Perform a ballad", ActionType = RulesetActionType.SkillCheck,
            Parameters = new Dictionary<string, string> { ["skill"] = "Performance", ["dc"] = "12" }
        };
        foreach (var (key, value) in extra)
        {
            action.Parameters[key] = value;
        }

        return action;
    }

    private static CrowdDraw.Beat? Strongest(IEnumerable<WorldChange> changes) =>
        CrowdDraw.Strongest(new PerceptibleBeats(null).Detect(changes, RulesetSystem.Dnd5e));

    [Fact]
    public void A_performance_draws_a_crowd_unless_it_says_notice()
    {
        var beat = Strongest([Perform()]);
        Assert.NotNull(beat);
        Assert.True(beat.Draws);
        Assert.Equal(20, beat.TotalModifier);

        Assert.Null(Strongest([Perform(("attention", "notice"))]));
    }

    [Fact]
    public void The_llms_crowd_modifier_is_added_and_clamped()
    {
        Assert.Equal(20 - 30, Strongest([Perform(("crowdModifier", "-30"))])!.TotalModifier);
        Assert.Equal(20 + 50, Strongest([Perform(("crowdModifier", "+80"))])!.TotalModifier);
    }

    [Fact]
    public void A_draw_outranks_a_merely_loud_beat_and_a_quiet_one_pulls_nobody()
    {
        var shout = new EventOccurred { Summary = "A shout", InitiatorId = "chars/a", Perceptible = "loud" };
        var quarrel = new EventOccurred { Summary = "A shouting match", InitiatorId = "chars/b", Attention = "draw", CrowdModifier = 10 };
        var whisper = new EventOccurred { Summary = "A whisper", InitiatorId = "chars/c" };

        var loud = Strongest([shout]);
        Assert.False(loud!.Draws);
        Assert.Equal(10, loud.TotalModifier);

        var best = Strongest([shout, quarrel]);
        Assert.Equal("chars/b", best!.ActorId);
        Assert.Equal(30, best.TotalModifier);

        Assert.Null(Strongest([whisper]));
    }

    [Fact]
    public void A_loud_spell_feeds_the_crowd_draw_and_a_quiet_one_does_not()
    {
        var spells = new SpellDefinitionProvider(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_crowd_spells_" + Guid.NewGuid()), typeof(SpellDefinitionProvider).Assembly);
        RulesetAction Cast(string name) => new() { CharacterId = "chars/mage", ActionName = name, ActionType = RulesetActionType.Spell };

        var boom = CrowdDraw.Strongest(new PerceptibleBeats(spells).Detect([Cast("fireball")], RulesetSystem.Dnd5e));
        Assert.NotNull(boom);
        Assert.False(boom.Draws);
        Assert.Equal(10, boom.TotalModifier);

        Assert.Null(CrowdDraw.Strongest(new PerceptibleBeats(spells).Detect([Cast("cure_wounds")], RulesetSystem.Dnd5e)));
    }

    // ---- S2: the engine rolls it ----

    private sealed record Tavern(string Slug, CampaignTools Tools, string LocId, string PcId);

    private async Task<Tavern> SeedTavernAsync(string? crowd = "a crowd of drinkers and dice players")
    {
        var slug = "crowd-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var locId = $"locations/{slug}-tavern";
        var pcId = $"chars/{slug}-maeve";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = locId, Name = "The Gilded Gull", Description = "A low taproom, smoke and fiddle music.", AmbientCrowd = crowd
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = pcId, Name = "Maeve", IsPc = true, CurrentLocationId = locId, MaxHp = 12, CurrentHp = 12,
                SystemStats = new Dnd5eExtension { ArmorClass = 12 }
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = $"chars/{slug}-barkeep", Name = "Holt", CurrentLocationId = locId, MaxHp = 9, CurrentHp = 9
            });
            await session.SaveChangesAsync();
        }

        await tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = locId }, slug);
        return new Tavern(slug, tools, locId, pcId);
    }

    private static Task<ToolResult<TurnResult>> SingAsync(Tavern t, int? crowdModifier = null, int? minutes = null)
    {
        var action = Perform();
        action.CharacterId = t.PcId;
        action.MinutesElapsed = minutes;
        if (crowdModifier != null)
        {
            action.Parameters["crowdModifier"] = crowdModifier.Value.ToString();
        }

        return t.Tools.TakeTurn(new TakeTurnRequest { Changes = [action], Narrative = "Maeve climbs onto a table and sings." }, t.Slug);
    }

    private static string Messages(ToolResult<TurnResult> result) => string.Join("\n", result.Data?.Summary ?? []);

    private static async Task<T> WithRollAsync<T>(double roll, Func<Task<T>> body)
    {
        EncounterResolver.RollOverrideForTests = () => roll;
        try
        {
            return await body();
        }
        finally
        {
            EncounterResolver.RollOverrideForTests = null;
        }
    }

    [Fact]
    public async Task A_song_in_a_crowded_tavern_brings_someone_over_who_knows_why_they_came()
    {
        var t = await SeedTavernAsync();

        var sing = await WithRollAsync(0.0, () => SingAsync(t, crowdModifier: 15));

        Assert.True(sing.Success, sing.Summary);
        Assert.Contains("CROWD DRAW at The Gilded Gull (modifier +20 engine +15 yours = +35)!", Messages(sing));

        using var session = _fixture.Store.OpenAsyncSession();
        var figures = await session.Query<Character>()
            .Where(c => c.CampaignName == t.Slug && c.Name == "Figure from the Crowd")
            .ToListAsync(TestContext.Current.CancellationToken);
        var figure = Assert.Single(figures);
        Assert.Contains("Drawn by Maeve", figure.Notes);
    }

    [Fact]
    public async Task A_miss_says_so_and_the_cooldown_stops_an_immediate_reroll()
    {
        var t = await SeedTavernAsync();

        var first = await WithRollAsync(0.99, () => SingAsync(t));
        var second = await WithRollAsync(0.0, () => SingAsync(t));

        Assert.True(first.Success, first.Summary);
        Assert.Contains("CROWD DRAW at The Gilded Gull (modifier +20): nobody comes over this time.", Messages(first));
        Assert.True(second.Success, second.Summary);
        Assert.Contains("Crowd draw at The Gilded Gull not rolled", Messages(second));
        Assert.DoesNotContain("CROWD DRAW", Messages(second));
    }

    [Fact]
    public async Task The_cooldown_says_this_clock_hour_and_lifts_once_the_clock_moves()
    {
        var t = await SeedTavernAsync();

        await WithRollAsync(0.99, () => SingAsync(t));
        var soon = await WithRollAsync(0.99, () => SingAsync(t, minutes: 30));
        var later = await WithRollAsync(0.99, () => SingAsync(t, minutes: 60));

        // Beats under an hour don't move the clock, so the engine can't say "30 min ago"; it says what it knows.
        Assert.Contains("the last one was earlier this clock hour (cooldown 60 min)", Messages(soon));
        Assert.Contains("CROWD DRAW at The Gilded Gull (modifier +20): nobody comes over this time.", Messages(later));
    }

    [Fact]
    public async Task An_empty_room_draws_nobody()
    {
        var t = await SeedTavernAsync(crowd: null);

        var sing = await WithRollAsync(0.0, () => SingAsync(t));

        Assert.True(sing.Success, sing.Summary);
        Assert.DoesNotContain("rowd draw", Messages(sing), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_witness_line_says_the_song_is_drawing_attention()
    {
        var t = await SeedTavernAsync();

        var sing = await WithRollAsync(0.99, () => SingAsync(t));

        var line = Assert.Single(sing.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.StartsWith("Witnesses (perform a ballad by Maeve, loud+visible, drawing attention): Holt", line);
    }
}
