using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Context;
using CampaignVault.Data.Templates;
using CampaignVault.Rulesets;
using CampaignVault.Services;
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
    private async Task<Camp> SeedCampAsync(bool withTraders = true, string? correnCondition = null)
    {
        var slug = "witness-" + Guid.NewGuid().ToString("N")[..8];
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
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
                    SystemStats = correnCondition == null
                        ? null
                        : new Dnd5eExtension { StatusEffects = [new StatusEffect { Name = correnCondition, Category = "Condition", ConditionName = correnCondition }] }
                });
                await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
                {
                    Id = $"chars/{slug}-ilsa", Name = "Ilsa", CurrentLocationId = locId, MaxHp = 9, CurrentHp = 9
                });
            }
            await session.SaveChangesAsync();
        }

        await tools.TakeTurn(new TakeTurnRequest { FullDetailLocationId = locId }, slug);
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
}
