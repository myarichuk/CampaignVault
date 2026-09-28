using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>The modifier pipeline: providers, advantage cancelling, willpower, and the resolvers that use it.</summary>
public class RollModifierPipelineTests
{
    private sealed class Fixed(params RollModifier[] mods) : IRollModifierProvider
    {
        public IEnumerable<RollModifier> Modifiers(RollQuery query) => mods;
    }

    private sealed class Throws : IRollModifierProvider
    {
        public IEnumerable<RollModifier> Modifiers(RollQuery query) => throw new System.InvalidOperationException("boom");
    }

    private static Character Hero(float willpower = 75f, params StatusEffect[] effects)
    {
        var c = new Character { Id = "chars/hero", Name = "Hero", SystemStats = new Dnd5eExtension { Willpower = willpower } };
        c.SystemStats.StatusEffects.AddRange(effects);
        return c;
    }

    private static RollQuery Save(Character c, string subject = "Wisdom", params string[] tags) =>
        new(RollKinds.Save, subject, tags, c, null, "dnd5e", new Dictionary<string, string>());

    [Fact]
    public void With_no_plugin_providers_it_is_just_the_status_fold()
    {
        var hero = Hero(75f, new StatusEffect { Name = "x", StatModifiers = { ["AllSaves"] = -1 } });
        var r = RollModifierPipeline.BuiltIn.Resolve(Save(hero), 3);

        Assert.Equal(2, r.Bonus);
        Assert.Equal(AdvantageEffect.None, r.Advantage);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Providers_add_and_explain_themselves()
    {
        var pipeline = new RollModifierPipeline([new Fixed(new RollModifier("p", 2, AdvantageEffect.None, "Blessed: +2"))]);
        var r = pipeline.Resolve(Save(Hero()), 1);

        Assert.Equal(3, r.Bonus);
        Assert.Equal(["Blessed: +2"], r.Notes);
    }

    [Theory]
    [InlineData(AdvantageEffect.Advantage, AdvantageEffect.Disadvantage, AdvantageEffect.None)]
    [InlineData(AdvantageEffect.Advantage, AdvantageEffect.None, AdvantageEffect.Advantage)]
    [InlineData(AdvantageEffect.None, AdvantageEffect.Disadvantage, AdvantageEffect.Disadvantage)]
    public void Any_advantage_and_any_disadvantage_cancel(AdvantageEffect a, AdvantageEffect b, AdvantageEffect net)
    {
        var pipeline = new RollModifierPipeline([
            new Fixed(new RollModifier("a", 0, a, "")),
            new Fixed(new RollModifier("b", 0, b, "")),
        ]);

        Assert.Equal(net, pipeline.Resolve(Save(Hero()), 0).Advantage);
    }

    [Fact]
    public void The_callers_explicit_advantage_is_one_more_source()
    {
        var pipeline = new RollModifierPipeline([new Fixed(new RollModifier("p", 0, AdvantageEffect.Disadvantage, "Poisoned"))]);

        Assert.Equal(AdvantageEffect.None, pipeline.Resolve(Save(Hero()), 0, AdvantageEffect.Advantage).Advantage);
        Assert.Equal(AdvantageEffect.Disadvantage, pipeline.Resolve(Save(Hero()), 0, AdvantageEffect.None).Advantage);
    }

    [Fact]
    public void A_provider_that_throws_is_skipped_not_fatal()
    {
        var pipeline = new RollModifierPipeline([new Throws(), new Fixed(new RollModifier("p", 1, AdvantageEffect.None, "ok"))]);

        Assert.Equal(1, pipeline.Resolve(Save(Hero()), 0).Bonus);
    }

    [Theory]
    [InlineData(95f, "charm", 1)]
    [InlineData(75f, "charm", 0)]
    [InlineData(45f, "fear", -1)]
    [InlineData(20f, "compulsion", -2)]
    [InlineData(5f, "mental", -3)]
    public void Willpower_bands_move_mental_saves(float willpower, string tag, int expected)
    {
        var r = RollModifierPipeline.BuiltIn.Resolve(Save(Hero(willpower), "Wisdom", tag), 0);

        Assert.Equal(expected, r.Bonus);
        Assert.Equal(willpower < 10 ? AdvantageEffect.Disadvantage : AdvantageEffect.None, r.Advantage);
        Assert.Equal(expected != 0, r.Notes.Any(n => n.StartsWith("Willpower")));
    }

    [Fact]
    public void Willpower_leaves_untagged_saves_and_other_rolls_alone()
    {
        var weak = Hero(5f);

        Assert.Equal(0, RollModifierPipeline.BuiltIn.Resolve(Save(weak, "Dexterity"), 0).Bonus);
        Assert.Equal(0, RollModifierPipeline.BuiltIn.Resolve(
            new RollQuery(RollKinds.Attack, null, ["fear"], weak, null, "dnd5e", new Dictionary<string, string>()), 0).Bonus);
    }

    [Fact]
    public void Speed_folds_armour_status_effects_and_providers_and_never_reaches_zero()
    {
        var hero = Hero(75f, new StatusEffect { Name = "hobbled", StatModifiers = { ["Speed"] = -20 } });
        hero.SystemStats.Movement = 30;
        hero.SystemStats.MovementModifier = -5;
        var pipeline = new RollModifierPipeline([]);
        var options = new Dictionary<string, string>();

        Assert.Equal(5, pipeline.Speed(hero, options, "dnd5e")); // 30 - 5 - 20
        Assert.Equal(10, pipeline.Speed(hero, options, "dnd5e", includeArmor: false)); // 30 - 20
        hero.SystemStats.StatusEffects.Add(new StatusEffect { Name = "worse", StatModifiers = { ["Speed"] = -20 } });
        Assert.Equal(5, pipeline.Speed(hero, options, "dnd5e")); // floor
        hero.SystemStats.Movement = null;
        Assert.Equal(0, pipeline.Speed(hero, options, "dnd5e")); // unknown: no speed to talk about
    }

    // ---- through the real resolver ----

    private static ChangeContext Ctx(params Character[] characters) =>
        new(
            sessionForTests: null,
            characters: characters.ToDictionary(c => c.Id),
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);

    private static async Task<(RollRequest Request, string Narrative)> RollSave(
        Character actor, string save, string? saveTags = null, RollModifierPipeline? pipeline = null, AdvantageState? adv = null)
    {
        RollRequest? seen = null;
        var roll = Substitute.For<IRollService>();
        roll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            seen = ci.Arg<RollRequest>();
            return Task.FromResult(new RollOutcome { Result = 12, Summary = "r" });
        });
        var resolver = new Dnd5eRulesetResolver(roll, rollModifiers: pipeline);
        var parameters = new Dictionary<string, string> { ["dc"] = "15", ["save"] = save };
        if (saveTags is not null)
            parameters["saveTags"] = saveTags;
        var output = await resolver.ResolveAsync(
            Ctx(actor),
            new RulesetAction
            {
                CharacterId = actor.Id, ActionType = RulesetActionType.SavingThrow, ActionName = "Will", Parameters = parameters,
                AdvantageState = adv ?? AdvantageState.None,
            },
            TestContext.Current.CancellationToken);
        return (seen!, output.Result.Narrative);
    }

    [Fact]
    public async Task A_broken_will_makes_a_fear_save_worse_and_the_roll_says_why()
    {
        var hero = Hero(5f);
        var (request, narrative) = await RollSave(hero, "Wisdom", saveTags: "fear");

        Assert.Equal(-3, request.Bonus); // wisdom 10 (+0) and willpower band
        Assert.Equal(DiceMechanic.Disadvantage, request.Mechanic);
        Assert.Contains("Willpower 5", narrative);
    }

    [Fact]
    public async Task The_same_save_is_untouched_for_a_default_character_and_when_untagged_for_a_dex_save()
    {
        var (request, narrative) = await RollSave(Hero(75f), "Wisdom", saveTags: "fear");
        Assert.Equal(0, request.Bonus);
        Assert.Equal(DiceMechanic.Standard, request.Mechanic);
        Assert.DoesNotContain("Willpower", narrative);

        var (dex, _) = await RollSave(Hero(5f), "Dexterity");
        Assert.Equal(0, dex.Bonus);
    }

    [Fact]
    public async Task A_wisdom_save_is_mental_even_without_tags()
    {
        var (request, _) = await RollSave(Hero(20f), "Wisdom");

        Assert.Equal(-2, request.Bonus);
    }

    [Fact]
    public async Task Callers_advantage_and_a_providers_disadvantage_cancel_in_the_real_roll()
    {
        var pipeline = new RollModifierPipeline([new Fixed(new RollModifier("p", 0, AdvantageEffect.Disadvantage, "Frightened"))]);
        var (request, narrative) = await RollSave(Hero(), "Dexterity", pipeline: pipeline, adv: AdvantageState.Advantage);

        Assert.Equal(DiceMechanic.Standard, request.Mechanic);
        Assert.Contains("Frightened", narrative);
    }

    [Fact]
    public void The_change_context_hands_plugins_the_same_pipeline()
    {
        var pipeline = new RollModifierPipeline([new Fixed(new RollModifier("p", 4, AdvantageEffect.None, "Plugin: +4"))]);
        var ctx = new ChangeContext(
            sessionForTests: null,
            characters: new Dictionary<string, Character>(),
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher(
                new IWorldChangeHandler[0], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance,
                rollModifiers: pipeline),
            campaignName: null);

        IChangeContext asPlugin = ctx;
        var r = asPlugin.ResolveRollModifiers(Save(Hero()), 1);

        Assert.Equal(5, r.Bonus);
        Assert.Equal(["Plugin: +4"], r.Notes);
    }

    [Fact]
    public async Task The_shipped_fear_spell_carries_its_tag_so_a_broken_will_feels_it_and_the_roll_names_it()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv-spells-" + System.Guid.NewGuid().ToString("N"));
        var provider = new SpellDefinitionProvider(dir, typeof(SpellDefinitionProvider).Assembly);
        Assert.True(provider.TryGet("dnd5e", "fear", out var fear));
        Assert.Contains("fear", fear!.Tags);

        var caster = new Character { Id = "chars/caster", Name = "Caster", SystemStats = new Dnd5eExtension { Level = 5 } };
        var victim = Hero(20f);
        RollRequest? seen = null;
        var roll = Substitute.For<IRollService>();
        roll.RollAsync(Arg.Any<RollRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            seen = ci.Arg<RollRequest>();
            return Task.FromResult(new RollOutcome { Result = 12, Summary = "r" });
        });
        var resolver = new Dnd5eRulesetResolver(roll, spellDefinitionProvider: provider);

        var output = await resolver.ResolveAsync(
            Ctx(caster, victim),
            new RulesetAction
            {
                CharacterId = caster.Id, ActionType = RulesetActionType.Spell, ActionName = "Fear", TargetIds = [victim.Id],
                Parameters = new Dictionary<string, string> { ["save"] = "Wisdom", ["dc"] = "15", ["spellSaveDc"] = "15" },
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal(-2, seen!.Bonus);
        Assert.Contains("Willpower 20: -2 vs fear", output.Result.Narrative.Replace("\u2212", "-"));
    }
}
