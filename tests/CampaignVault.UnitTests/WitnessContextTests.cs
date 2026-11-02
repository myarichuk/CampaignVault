using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Context;
using CampaignVault.Data.Templates;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// WITNESS_CHANNEL_PLAN.md: a loud or visible beat names everyone else at the actor's location who perceived it,
/// so the narrator gives the camp a reaction instead of narrating only the fight.
/// </summary>
[Collection("RavenDB")]
public class WitnessContextTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public WitnessContextTests(RavenDBFixture fixture) => _fixture = fixture;

    private sealed record Camp(string Slug, CampaignTools Tools, string LocId, string PcId, string GoblinId);

    /// <summary>A night camp: the PC (Maeve), two traders who aren't fighting, and a goblin scout.</summary>
    private async Task<Camp> SeedCampAsync(bool withTraders = true, string? correnCondition = null, bool startCombat = true, int? correnPassive = null,
        IRollService? rolls = null)
    {
        var slug = "witness-" + Guid.NewGuid().ToString("N")[..8];
        var repo = rolls == null
            ? _fixture.CreateRepository()
            : _fixture.CreateRepository(overrides: b => b.RegisterInstance(rolls).As<IRollService>());
        var tools = TestCampaignToolsFactory.Create(_fixture, rollService: rolls, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-camp";
        var pcId = $"chars/{slug}-maeve";
        var goblinId = $"chars/{slug}-goblin";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = locId, Name = "Roadside Camp", Description = "Two wagons, a low fire, bedrolls in the grass."
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = pcId, Name = "Maeve", IsPc = true, CurrentLocationId = locId, MaxHp = 18, CurrentHp = 18,
                SystemStats = new Dnd5eExtension { ArmorClass = 12 }
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = goblinId, Name = "Goblin Scout", CurrentLocationId = locId, MaxHp = 7, CurrentHp = 7,
                SystemStats = new Dnd5eExtension { ArmorClass = 15, Dexterity = 14 }
            });
            if (withTraders)
            {
                await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
                {
                    Id = $"chars/{slug}-corren", Name = "Corren", CurrentLocationId = locId, MaxHp = 9, CurrentHp = 9,
                    SystemStats = CorrenStats(correnCondition, correnPassive)
                });
                await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
                {
                    Id = $"chars/{slug}-ilsa", Name = "Ilsa", CurrentLocationId = locId, MaxHp = 9, CurrentHp = 9
                });
            }
            await session.SaveChangesAsync();
        }

        await tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = locId }, slug);
        if (!startCombat)
        {
            return new Camp(slug, tools, locId, pcId, goblinId);
        }

        var started = await tools.StartCombat(locId, [pcId, goblinId], slug);
        Assert.True(started.Success, started.Summary);
        var camp = new Camp(slug, tools, locId, pcId, goblinId);
        await ToMaevesTurnAsync(camp, started.Data!.ActiveTurnId);
        return camp;
    }

    /// <summary>Initiative is rolled, so pass turns until it's the PC's.</summary>
    private static async Task ToMaevesTurnAsync(Camp c, string? active)
    {
        for (var i = 0; i < 4 && !string.Equals(active, c.PcId, StringComparison.OrdinalIgnoreCase); i++)
        {
            var next = await c.Tools.NextTurn(campaignName: c.Slug);
            Assert.True(next.Success, next.Summary);
            active = next.Data!.ActiveTurnId;
        }

        Assert.Equal(c.PcId, active, ignoreCase: true);
    }

    private static Dnd5eExtension? CorrenStats(string? condition, int? passive)
    {
        if (condition == null && passive == null)
        {
            return null;
        }

        var stats = new Dnd5eExtension();
        if (condition != null)
        {
            stats.StatusEffects.Add(new StatusEffect { Name = condition, Category = "Condition", ConditionName = condition });
        }

        if (passive != null)
        {
            stats.Attributes["passivePerception"] = passive.Value;
        }

        return stats;
    }

    private static Task<ToolResult<TurnResult>> CastFireballAsync(Camp c) =>
        c.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = c.PcId, TargetIds = [c.GoblinId], ActionType = RulesetActionType.Spell,
                    ActionCategory = ActionCategory.Spell, ActionName = "fireball",
                    Parameters = new Dictionary<string, string>
                    {
                        ["resolution"] = "save", ["save"] = "Dexterity", ["damageDice"] = "8d6", ["innate"] = "true"
                    }
                }
            ],
            Narrative = "Maeve's fireball blooms over the goblin at the edge of the firelight."
        }, c.Slug);

    [Fact]
    public async Task A_fireball_names_the_traders_who_saw_it_but_not_the_combatants()
    {
        var c = await SeedCampAsync();

        var cast = await CastFireballAsync(c);

        Assert.True(cast.Success, cast.Summary);
        var line = Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.StartsWith("Witnesses (fireball by Maeve, loud+visible):", line);
        var names = line[(line.IndexOf("):", StringComparison.Ordinal) + 2)..];
        Assert.Contains("Corren", names);
        Assert.Contains("Ilsa", names);
        Assert.DoesNotContain("Goblin Scout", names);
        Assert.DoesNotContain("Maeve", names);
    }

    [Fact]
    public async Task A_second_fireball_on_the_next_beat_gets_its_own_witness_line()
    {
        var c = await SeedCampAsync();

        var first = await CastFireballAsync(c);
        await ToMaevesTurnAsync(c, active: null);
        var second = await CastFireballAsync(c);

        Assert.True(first.Success, first.Summary);
        Assert.True(second.Success, second.Summary);
        Assert.Contains(first.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.Contains(second.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
    }

    [Fact]
    public async Task Nobody_else_at_the_camp_means_no_witness_line()
    {
        var c = await SeedCampAsync(withTraders: false);

        var cast = await CastFireballAsync(c);

        Assert.True(cast.Success, cast.Summary);
        Assert.DoesNotContain(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
    }

    [Fact]
    public async Task A_deafened_trader_only_saw_it_and_an_unconscious_one_perceived_nothing()
    {
        var deaf = await SeedCampAsync(correnCondition: "deafened");
        var line = Assert.Single((await CastFireballAsync(deaf)).Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.Contains("Corren (deafened: saw it only)", line);

        var out_ = await SeedCampAsync(correnCondition: "unconscious");
        var line2 = Assert.Single((await CastFireballAsync(out_)).Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.DoesNotContain("Corren", line2);
        Assert.Contains("Ilsa", line2);
    }

    [Fact]
    public async Task A_loud_event_with_no_spell_names_the_bystanders()
    {
        var c = await SeedCampAsync();

        var shout = await c.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new EventOccurred
                {
                    Summary = "Maeve shouts a warning", Category = EventCategory.Conversation, InitiatorId = c.PcId,
                    Involved = [c.PcId], LocationId = c.LocId, Perceptible = "loud"
                }
            ],
            Narrative = "Maeve shouts for everyone to wake."
        }, c.Slug);

        Assert.True(shout.Success, shout.Summary);
        var line = Assert.Single(shout.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.StartsWith("Witnesses (Maeve shouts a warning, loud):", line);
        Assert.Contains("Corren", line);
    }

    [Fact]
    public async Task Fighting_is_announced_to_the_bystanders_once_per_fight_not_every_round()
    {
        var c = await SeedCampAsync();
        Task<ToolResult<TurnResult>> Swing() => c.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = c.PcId, TargetIds = [c.GoblinId], ActionType = RulesetActionType.Attack,
                    ActionCategory = ActionCategory.Melee, ActionName = "Dagger",
                    Parameters = new Dictionary<string, string> { ["attackBonus"] = "4", ["damageDice"] = "1d4" }
                }
            ],
            Narrative = "Maeve slashes at the goblin."
        }, c.Slug);

        var first = await Swing();
        await ToMaevesTurnAsync(c, active: null);
        var second = await Swing();

        Assert.True(first.Success, first.Summary);
        Assert.True(second.Success, second.Summary);
        var line = Assert.Single(first.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.StartsWith("Witnesses (a fight breaks out, loud+visible):", line);
        Assert.DoesNotContain(second.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
    }

    // ---- A1–A4: sleep, waking and hiding ----

    private static Task<ToolResult<TurnResult>> CommitAsync(Camp c, string narrative, params WorldChange[] changes) =>
        c.Tools.TakeTurn(new TakeTurnRequest { Changes = [.. changes], Narrative = narrative }, c.Slug);

    private static string Messages(ToolResult<TurnResult> result) => string.Join("\n", result.Data?.Summary ?? []);

    private async Task<Character> LoadAsync(string id)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        return (await session.LoadAsync<Character>(id, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task A_sleeping_trader_is_woken_by_the_fireball_and_listed_as_woken()
    {
        var c = await SeedCampAsync();
        var correnId = $"chars/{c.Slug}-corren";
        var sleep = await CommitAsync(c, "Corren dozes by the wagon.", new AwarenessChange { CharacterId = correnId, State = "asleep" });
        Assert.True(sleep.Success, sleep.Summary);
        Assert.Equal(AwarenessState.Asleep, (await LoadAsync(correnId)).Awareness?.State);

        var cast = await CastFireballAsync(c);

        Assert.True(cast.Success, cast.Summary);
        Assert.Contains("WOKEN by fireball: Corren", Messages(cast));
        Assert.Contains("Corren", Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses")));
        Assert.Null((await LoadAsync(correnId)).Awareness);
    }

    [Fact]
    public async Task A_sleeper_sleeps_through_a_silent_image()
    {
        var c = await SeedCampAsync(startCombat: false);
        var correnId = $"chars/{c.Slug}-corren";
        await CommitAsync(c, "Corren dozes by the wagon.", new AwarenessChange { CharacterId = correnId, State = "asleep" });

        var cast = await CommitAsync(c, "A ghostly wolf shape pads silently past the fire.",
            new RulesetAction
            {
                CharacterId = c.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "silent_image",
                Parameters = new Dictionary<string, string> { ["resolution"] = "utility", ["innate"] = "true" }
            });

        Assert.True(cast.Success, cast.Summary);
        var line = Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.StartsWith("Witnesses (silent image by Maeve, visible):", line);
        Assert.DoesNotContain("Corren", line);
        Assert.Equal(AwarenessState.Asleep, (await LoadAsync(correnId)).Awareness?.State);
    }

    /// <summary>A5: rest at the camp, interrupted, with the approach's Stealth pinned.</summary>
    private static async Task<ToolResult<TurnResult>> InterruptedRestAsync(Camp c, int approachStealth, params string[] watcherIds)
    {
        EncounterResolver.RollOverrideForTests = () => 0.0;
        EncounterResolver.ApproachStealthOverrideForTests = () => approachStealth;
        try
        {
            return await CommitAsync(c, "Maeve beds down by the fire.",
                new RestChange
                {
                    CharacterId = c.PcId, LocationId = c.LocId, IntendedHours = 8, RestType = RestType.LongRest,
                    WatcherIds = watcherIds.Length > 0 ? [.. watcherIds] : null
                });
        }
        finally
        {
            EncounterResolver.RollOverrideForTests = null;
            EncounterResolver.ApproachStealthOverrideForTests = null;
        }
    }

    [Fact]
    public async Task With_nobody_on_watch_a_quiet_approach_finds_the_pc_asleep_and_surprised()
    {
        var c = await SeedCampAsync(startCombat: false);

        var rest = await InterruptedRestAsync(c, approachStealth: 15);

        Assert.True(rest.Success, rest.Summary);
        var messages = Messages(rest);
        Assert.Contains("NOTICING (approach Stealth 15):", messages);
        Assert.Contains("did NOT notice: ", messages);
        Assert.Contains("Maeve (asleep, passive Perception 5 vs Stealth 15)", messages);
        Assert.Contains("surprisedIds", messages);
        Assert.DoesNotContain("woke to it", messages);
        Assert.Equal(AwarenessState.Asleep, (await LoadAsync(c.PcId)).Awareness?.State);
    }

    [Fact]
    public async Task A_sharp_watcher_notices_what_the_sleeper_does_not()
    {
        var c = await SeedCampAsync(startCombat: false, correnPassive: 16);
        var correnId = $"chars/{c.Slug}-corren";

        var rest = await InterruptedRestAsync(c, 15, correnId);

        var messages = Messages(rest);
        Assert.Contains("noticed it: ", messages);
        Assert.Contains("Corren (on watch, passive Perception 16 vs Stealth 15)", messages);
        Assert.Contains("Maeve (asleep, passive Perception 5 vs Stealth 15)", messages);
        Assert.Null((await LoadAsync(correnId)).Awareness);
    }

    [Fact]
    public async Task A_clumsy_approach_wakes_the_sleeper()
    {
        var c = await SeedCampAsync(startCombat: false);

        var rest = await InterruptedRestAsync(c, approachStealth: 3);

        Assert.Contains("woke to it: ", Messages(rest));
        Assert.Contains("Maeve (asleep, passive Perception 5 vs Stealth 3)", Messages(rest));
        Assert.Null((await LoadAsync(c.PcId)).Awareness);
    }

    [Fact]
    public async Task A_stealth_check_hides_the_actor_until_they_attack()
    {
        var c = await SeedCampAsync(startCombat: false);

        var sneak = await CommitAsync(c, "Maeve melts into the dark beyond the firelight.",
            new RulesetAction
            {
                CharacterId = c.PcId, ActionName = "Hide", ActionType = RulesetActionType.SkillCheck,
                Parameters = new Dictionary<string, string> { ["skill"] = "Stealth", ["dc"] = "10" }
            });
        Assert.True(sneak.Success, sneak.Summary);
        var hidden = (await LoadAsync(c.PcId)).Concealment;
        Assert.NotNull(hidden);

        var stab = await CommitAsync(c, "Maeve lunges from the dark.",
            new RulesetAction
            {
                CharacterId = c.PcId, TargetIds = [c.GoblinId], ActionType = RulesetActionType.Attack, ActionCategory = ActionCategory.Melee,
                ActionName = "Dagger",
                Parameters = new Dictionary<string, string> { ["attackBonus"] = "4", ["damageDice"] = "1d4", ["outsideCombat"] = "true" }
            });

        Assert.True(stab.Success, stab.Summary);
        Assert.Null((await LoadAsync(c.PcId)).Concealment);
    }

    [Fact]
    public async Task Awareness_change_rejects_an_unknown_state_and_reveals_a_hider()
    {
        var c = await SeedCampAsync(startCombat: false);

        var bad = await CommitAsync(c, "?", new AwarenessChange { CharacterId = c.PcId, State = "drowsy" });
        Assert.False(bad.Success);

        await CommitAsync(c, "Maeve hides.",
            new RulesetAction
            {
                CharacterId = c.PcId, ActionName = "Hide", ActionType = RulesetActionType.SkillCheck,
                Parameters = new Dictionary<string, string> { ["skill"] = "Stealth", ["dc"] = "10" }
            });
        var spotted = await CommitAsync(c, "A lantern swings her way.", new AwarenessChange { CharacterId = c.PcId, Revealed = true });

        Assert.True(spotted.Success, spotted.Summary);
        Assert.Null((await LoadAsync(c.PcId)).Concealment);
    }

    // ---- A6: sneaking and sleight of hand, PCs included ----

    private sealed class FixedRollService(int result) : IRollService
    {
        public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RollOutcome { Tag = request.Tag, Result = result + request.Bonus });

        public async Task<IReadOnlyList<RollOutcome>> RollBatchAsync(IEnumerable<RollRequest> requests, CancellationToken ct = default)
        {
            var outcomes = new List<RollOutcome>();
            foreach (var request in requests)
            {
                outcomes.Add(await RollAsync(request, ct));
            }

            return outcomes;
        }
    }

    private static RulesetAction GoblinCheck(Camp c, string skill) => new()
    {
        CharacterId = c.GoblinId, ActionName = skill, ActionType = RulesetActionType.SkillCheck,
        Parameters = new Dictionary<string, string> { ["skill"] = skill, ["bonus"] = "2", ["dc"] = "10" }
    };

    [Fact]
    public async Task A_haggling_pc_misses_the_pickpocket_that_a_sharp_trader_catches()
    {
        var c = await SeedCampAsync(startCombat: false, correnPassive: 16, rolls: new FixedRollService(10));
        var ilsaId = $"chars/{c.Slug}-ilsa";

        var beat = await CommitAsync(c, "While Maeve haggles with Ilsa over a bolt of wool, the goblin's fingers find her purse.",
            new EventOccurred { Summary = "Maeve haggles with Ilsa", Category = EventCategory.Conversation, Involved = [c.PcId, ilsaId] },
            GoblinCheck(c, "Sleight of Hand"));

        Assert.True(beat.Success, beat.Summary);
        var line = Assert.Single(beat.Data!.Context ?? [], l => l.StartsWith("Noticing Goblin Scout's Sleight of Hand (12)"));
        Assert.Contains("notices: Corren (passive Perception 16 vs Sleight of Hand 12)", line);
        Assert.Contains("Maeve (distracted, passive Perception 5 vs Sleight of Hand 12)", line);
        Assert.Contains("Ilsa (distracted, passive Perception 5 vs Sleight of Hand 12)", line);
    }

    [Fact]
    public async Task A_sneaking_goblin_is_noticed_by_whoever_beats_its_stealth_pc_or_not()
    {
        var c = await SeedCampAsync(startCombat: false, correnPassive: 16, rolls: new FixedRollService(10));

        var sneak = await CommitAsync(c, "The goblin creeps along the wagon's shadow.", GoblinCheck(c, "Stealth"));

        Assert.True(sneak.Success, sneak.Summary);
        var line = Assert.Single(sneak.Data!.Context ?? [], l => l.StartsWith("Noticing Goblin Scout's Stealth (12)"));
        Assert.Contains("notices: Corren (passive Perception 16 vs Stealth 12)", line);
        Assert.Contains("does NOT notice: ", line);
        Assert.Contains("Maeve (passive Perception 10 vs Stealth 12)", line);
    }

    // ---- W2: what a cast gives off ----

    private static readonly SpellDefinitionProvider Spells = new(
        Path.Combine(Path.GetTempPath(), "cv_witness_spells_" + Guid.NewGuid()),
        typeof(SpellDefinitionProvider).Assembly);

    private static Perceptible Of(string spell, string? mode = null)
    {
        Spells.TryGet(RulesetSystem.Dnd5e, spell, out var definition);
        var action = new RulesetAction { CharacterId = "chars/x", ActionName = spell, ActionType = RulesetActionType.Spell };
        if (mode != null)
        {
            action.Parameters["mode"] = mode;
        }

        return Perceptibility.OfSpell(definition, action);
    }

    [Theory]
    [InlineData("fireball", null, Perceptible.Both)]
    [InlineData("minor_illusion", "image", Perceptible.Visible)]
    [InlineData("minor_illusion", "sound", Perceptible.Loud)]
    [InlineData("minor_illusion", null, Perceptible.Both)]
    [InlineData("cure_wounds", null, Perceptible.None)]
    [InlineData("thunderwave", null, Perceptible.Both)]
    public void A_spell_gives_off_what_its_data_says(string spell, string? mode, Perceptible expected) =>
        Assert.Equal(expected, Of(spell, mode));

    [Theory]
    [InlineData("dnd5e")]
    [InlineData("pf2e")]
    public void Every_generated_spell_says_what_it_gives_off(string system)
    {
        var missing = Spells.GetSpellsForSystem(system).Values.Where(s => s.Perceptible == null).Select(s => s.Name).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("fireball", null, Perceptible.Both)]
    [InlineData("figment", "Sound", Perceptible.Loud)]
    [InlineData("figment", null, Perceptible.Both)]
    [InlineData("illusory_object", null, Perceptible.Visible)]
    [InlineData("heal", null, Perceptible.None)]
    public void A_pf2e_spell_gives_off_what_its_traits_say(string spell, string? mode, Perceptible expected)
    {
        Spells.TryGet(RulesetSystem.Pathfinder2e, spell, out var definition);
        Assert.NotNull(definition);
        var action = new RulesetAction { CharacterId = "chars/x", ActionName = spell, ActionType = RulesetActionType.Spell };
        if (mode != null)
        {
            action.Parameters["mode"] = mode;
        }

        Assert.Equal(expected, Perceptibility.OfSpell(definition, action));
    }

    [Fact]
    public void The_spells_own_field_and_modes_win_over_the_derivation()
    {
        var action = new RulesetAction { CharacterId = "chars/x", ActionName = "x", ActionType = RulesetActionType.Spell };
        Assert.Equal(Perceptible.None, Perceptibility.OfSpell(new SpellDefinition { Name = "x", School = "illusion", Perceptible = "none" }, action));

        var modal = new SpellDefinition { Name = "x", PerceptibleModes = new() { ["image"] = "visible", ["sound"] = "loud" } };
        Assert.Equal(Perceptible.Both, Perceptibility.OfSpell(modal, action));
        action.Parameters["mode"] = "sound";
        Assert.Equal(Perceptible.Loud, Perceptibility.OfSpell(modal, action));
    }
    // ---- W5: earshot one exit away ----

    /// <summary>A ridge one exit from the camp, <paramref name="travelHours"/> away, with a lookout (Bram) standing on it.</summary>
    private async Task<string> AddRidgeAsync(Camp c, double travelHours)
    {
        var ridgeId = $"locations/{c.Slug}-ridge";
        var bramId = $"chars/{c.Slug}-bram";
        var repo = _fixture.CreateRepository();
        using var session = _fixture.Store.OpenAsyncSession();
        var cs = _fixture.CreateCampaignSession(session, c.Slug);
        await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
        {
            Id = ridgeId, Name = "the Ridge", Description = "A bare ridge above the road.",
            Exits = [new LocationExit(c.LocId, "Down to the camp", TravelCostHours: travelHours)]
        });
        var camp = await session.LoadAsync<Location>(c.LocId, TestContext.Current.CancellationToken);
        camp!.Exits.Add(new LocationExit(ridgeId, "Up to the ridge", TravelCostHours: travelHours));
        await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
        {
            Id = bramId, Name = "Bram", CurrentLocationId = ridgeId, MaxHp = 9, CurrentHp = 9
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return bramId;
    }

    [Fact]
    public async Task A_fireball_is_heard_from_the_ridge_next_to_the_camp()
    {
        var c = await SeedCampAsync();
        await AddRidgeAsync(c, travelHours: 0.1);

        var cast = await CastFireballAsync(c);

        Assert.True(cast.Success, cast.Summary);
        var near = Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Heard from"));
        Assert.StartsWith("Heard from Roadside Camp (fireball): Bram at the Ridge.", near);
        Assert.DoesNotContain("Bram", Assert.Single(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses")));
    }

    [Fact]
    public async Task A_silent_image_does_not_reach_the_ridge()
    {
        var c = await SeedCampAsync(startCombat: false);
        await AddRidgeAsync(c, travelHours: 0.1);

        var cast = await CommitAsync(c, "A ghostly wolf shape pads silently past the fire.",
            new RulesetAction
            {
                CharacterId = c.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "silent_image",
                Parameters = new Dictionary<string, string> { ["resolution"] = "utility", ["innate"] = "true" }
            });

        Assert.True(cast.Success, cast.Summary);
        Assert.Contains(cast.Data!.Context ?? [], l => l.StartsWith("Witnesses"));
        Assert.DoesNotContain(cast.Data!.Context ?? [], l => l.StartsWith("Heard from"));
    }

    [Fact]
    public async Task A_far_neighbour_and_a_sleeping_one_hear_nothing()
    {
        var far = await SeedCampAsync();
        await AddRidgeAsync(far, travelHours: 1);
        Assert.DoesNotContain((await CastFireballAsync(far)).Data!.Context ?? [], l => l.StartsWith("Heard from"));

        var asleep = await SeedCampAsync();
        var bramId = await AddRidgeAsync(asleep, travelHours: 0.1);
        await CommitAsync(asleep, "Bram dozes at his post.", new AwarenessChange { CharacterId = bramId, State = "asleep" });
        Assert.DoesNotContain((await CastFireballAsync(asleep)).Data!.Context ?? [], l => l.StartsWith("Heard from"));
    }
    // ---- W8: the playtest that started this, end to end ----

    [Fact]
    public async Task Playtest_the_illusion_then_the_fireball_both_reach_the_camp()
    {
        var c = await SeedCampAsync();
        await AddRidgeAsync(c, travelHours: 0.1);

        var illusion = await c.Tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new RulesetAction
                {
                    CharacterId = c.PcId, ActionType = RulesetActionType.Spell, ActionCategory = ActionCategory.Spell, ActionName = "minor_illusion",
                    Parameters = new Dictionary<string, string> { ["resolution"] = "utility", ["innate"] = "true", ["mode"] = "sound" }
                }
            ],
            Narrative = "Maeve throws a wolf's howl into the dark behind the goblin."
        }, c.Slug);
        await ToMaevesTurnAsync(c, active: null);
        var fireball = await CastFireballAsync(c);

        Assert.True(illusion.Success, illusion.Summary);
        Assert.True(fireball.Success, fireball.Summary);
        foreach (var (beat, result) in new[] { ("minor illusion", illusion), ("fireball", fireball) })
        {
            foreach (var line in (result.Data!.Context ?? []).Where(l => l.StartsWith("Witnesses") || l.StartsWith("Heard from")))
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"[{beat}] {line}");
            }
        }

        Assert.StartsWith("Witnesses (minor illusion by Maeve, loud):", Assert.Single(illusion.Data!.Context ?? [], l => l.StartsWith("Witnesses")));
        Assert.StartsWith("Heard from Roadside Camp (minor illusion):", Assert.Single(illusion.Data!.Context ?? [], l => l.StartsWith("Heard from")));
        Assert.StartsWith("Witnesses (fireball by Maeve, loud+visible):", Assert.Single(fireball.Data!.Context ?? [], l => l.StartsWith("Witnesses")));
        Assert.Contains(fireball.Data!.Context ?? [], l => l.StartsWith("Heard from Roadside Camp (fireball)"));
    }
}
