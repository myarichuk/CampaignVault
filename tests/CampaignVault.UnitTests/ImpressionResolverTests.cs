using System;
using System.Collections.Generic;
using System.Linq;
using CampaignVault.Data.Impressions;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>IMPRESSIONS_PLAN.md I4 (appeal, read) and I5 (appraise), as pure functions.</summary>
public class ImpressionResolverTests
{
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);

    private static Character Person(string id = "chars/o", int wisdom = 10, int insight = int.MinValue, LifeStage stage = LifeStage.Adult,
        string[]? traits = null, string[]? fears = null, string[]? wants = null, float morale = 65f, int hp = 10, int maxHp = 10)
    {
        var stats = new Dnd5eExtension { Wisdom = wisdom, Morale = morale };
        if (insight != int.MinValue)
        {
            stats.SkillModifiers["Insight"] = insight;
        }

        var c = new Character { Id = id, Name = id.Split('/')[1], SystemStats = stats, LifeStage = stage, CurrentHp = hp, MaxHp = maxHp };
        c.Psychology.Traits = [.. traits ?? []];
        c.Psychology.Fears = [.. fears ?? []];
        c.Psychology.Wants = [.. wants ?? []];
        return c;
    }

    private static readonly Character Maeve = new()
    {
        Id = "chars/maeve", Name = "Maeve", SystemStats = new Dnd5eExtension { Charisma = 16, SkillModifiers = { ["Deception"] = 5 } }
    };

    private static Appeal AppealOf(Dictionary<string, string> parameters, RulesetActionType type = RulesetActionType.Spell, string name = "minor_illusion")
    {
        var appeal = Appeal.Of(new RulesetAction { CharacterId = Maeve.Id, ActionName = name, ActionType = type, Parameters = parameters }, out var error);
        Assert.Null(error);
        return Assert.IsType<Appeal>(appeal);
    }

    private static BeatFacts Facts(bool illusion = false, int? total = null, bool? won = null, string? target = null, int? dc = null, bool tied = false) =>
        new(illusion, total, won, target == null ? None : new HashSet<string>([target], StringComparer.OrdinalIgnoreCase), dc, RulesetSystem.Dnd5e, tied);

    // ---- I4: appeal ----

    [Fact]
    public void An_appeal_parses_its_tone_intensity_and_reception()
    {
        var appeal = AppealOf(new() { ["appeal"] = "Delight", ["intensity"] = "strong", ["subject"] = "butterflies", ["reception"] = "chars/a:disgust; chars/b=awe" });
        Assert.Equal("delight", appeal.Tone.Name);
        Assert.Equal(ImpressionTier.Strong, appeal.Intensity);
        Assert.False(appeal.Deceptive);
        Assert.Equal("disgust", appeal.Reception["CHARS/A"]);
        Assert.Equal("awe", appeal.Reception["chars/b"]);
    }

    [Theory]
    [InlineData("appeal", "glee", "isn't a tone")]
    [InlineData("intensity", "huge", "isn't mild, strong or overwhelming")]
    [InlineData("reception", "chars/a", "isn't 'characterId:tone'")]
    [InlineData("reception", "chars/a:scorn", "isn't a tone")]
    public void A_bad_appeal_says_what_is_wrong(string key, string value, string expected)
    {
        var parameters = new Dictionary<string, string> { ["appeal"] = "delight", [key] = value };
        Appeal.Of(new RulesetAction { CharacterId = Maeve.Id, ActionName = "x", ActionType = RulesetActionType.Spell, Parameters = parameters }, out var error);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("Intimidation", "menace", false)]
    [InlineData("Deception", "trust", true)]
    [InlineData("Performance", "delight", false)]
    public void Three_checks_say_what_they_are_for_without_an_appeal(string skill, string tone, bool deceptive)
    {
        var appeal = AppealOf(new() { ["skill"] = skill }, RulesetActionType.SkillCheck, skill);
        Assert.Equal(tone, appeal.Tone.Name);
        Assert.Equal(deceptive, appeal.Deceptive);
        Assert.True(appeal.Inferred);
    }

    [Fact]
    public void Nothing_to_strike_means_no_appeal_and_orphan_fields_are_an_error()
    {
        Assert.Null(Appeal.Of(new RulesetAction { CharacterId = Maeve.Id, ActionName = "Athletics", ActionType = RulesetActionType.SkillCheck }, out var none));
        Assert.Null(none);
        Appeal.Of(new EventOccurred { Summary = "x", Deceptive = true }, out var orphan);
        Assert.Contains("need an appeal", orphan);
    }

    // ---- I4: read ----

    [Fact]
    public void A_sincere_beat_lands_and_an_illusion_is_believed_with_the_check_named()
    {
        Assert.Equal(ImpressionRead.Sincere, ImpressionResolver.Read(Person(), Maeve, AppealOf(new() { ["appeal"] = "delight" }), Facts()).Read);

        var ghost = ImpressionResolver.Read(Person(), Maeve, AppealOf(new() { ["appeal"] = "fear", ["deceptive"] = "true" }), Facts(illusion: true, dc: 13));
        Assert.Equal(ImpressionRead.Believed, ghost.Read);
        Assert.Equal("believes it; examining it takes an action: Investigation vs DC 13", ghost.Note);
    }

    [Fact]
    public void A_bystander_sets_passive_insight_against_the_roll()
    {
        var lie = AppealOf(new() { ["skill"] = "Deception" }, RulesetActionType.SkillCheck, "Deception");
        var sharp = ImpressionResolver.Read(Person(wisdom: 14, insight: 5), Maeve, lie, Facts(total: 15));
        Assert.Equal(ImpressionRead.SawThrough, sharp.Read);
        Assert.Equal("saw through it: Insight 15 vs 15", sharp.Note);

        var dull = ImpressionResolver.Read(Person(wisdom: 8), Maeve, lie, Facts(total: 15));
        Assert.Equal(ImpressionRead.Believed, dull.Read);
        Assert.Equal("believes it: Insight 9 vs 15", dull.Note);
    }

    [Fact]
    public void With_no_roll_it_is_passive_insight_against_passive_deception()
    {
        var flattery = Assert.IsType<Appeal>(Appeal.Of(new EventOccurred { Summary = "x", Appeal = "pride", Deceptive = true }));
        Assert.Equal("believes it: Insight 10 vs 16 (no roll: the liar's average)", ImpressionResolver.Read(Person(), Maeve, flattery, Facts()).Note);
        // An untrained listener against an untrained liar: 10 doesn't meet the liar's average 10.5.
        var plain = new Character { Id = "chars/p", Name = "P", SystemStats = new Dnd5eExtension() };
        Assert.Equal(ImpressionRead.Believed, ImpressionResolver.Read(Person(), plain, flattery, Facts()).Read);
        Assert.Equal(ImpressionRead.SawThrough, ImpressionResolver.Read(Person(wisdom: 12), plain, flattery, Facts()).Read);
    }

    [Fact]
    public void The_contests_target_follows_the_contest_not_their_passive_score()
    {
        var lie = AppealOf(new() { ["skill"] = "Deception" }, RulesetActionType.ContestedCheck, "Deception");
        var guard = Person("chars/guard", wisdom: 20);
        Assert.Equal(ImpressionRead.Believed, ImpressionResolver.Read(guard, Maeve, lie, Facts(total: 3, won: true, target: guard.Id)).Read);
        Assert.Equal(ImpressionRead.SawThrough, ImpressionResolver.Read(Person("chars/x", wisdom: 1), Maeve, lie, Facts(total: 30, won: false, target: "chars/x")).Read);
    }

    // ---- review fixes ----

    [Theory]
    [InlineData("Persuade the lieutenant", "Persuasion")]
    [InlineData("Perform first aid", "Medicine")]
    [InlineData("Make them believe us", null)]
    public void Only_the_skill_itself_infers_an_appeal_not_a_word_in_the_label(string name, string? skill)
    {
        var parameters = skill == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["skill"] = skill };
        Assert.Null(Appeal.Of(new RulesetAction { CharacterId = Maeve.Id, ActionName = name, ActionType = RulesetActionType.SkillCheck, Parameters = parameters }));
    }

    [Fact]
    public void The_events_field_names_work_on_an_action_too()
    {
        var appeal = AppealOf(new() { ["appeal"] = "awe", ["appealIntensity"] = "strong", ["appealSubject"] = "lights" });
        Assert.Equal(ImpressionTier.Strong, appeal.Intensity);
        Assert.Equal("lights", appeal.Subject);
    }

    [Fact]
    public void A_tied_contest_leaves_its_target_unconvinced_not_insulted()
    {
        var lie = AppealOf(new() { ["skill"] = "Deception" }, RulesetActionType.ContestedCheck, "Deception");
        var read = ImpressionResolver.Read(Person("chars/g"), Maeve, lie, Facts(total: 12, won: false, target: "chars/g", tied: true));
        Assert.Equal(ImpressionRead.Suspects, read.Read);
        Assert.Equal("unconvinced (a tie)", read.Note);
    }

    [Fact]
    public void Fear_and_hypnotic_pattern_are_saves_not_figments()
    {
        var spells = new CampaignVault.Services.SpellDefinitionProvider(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_figment_" + Guid.NewGuid()), typeof(CampaignVault.Services.SpellDefinitionProvider).Assembly);
        foreach (var (name, figment) in new[] { ("fear", false), ("hypnotic_pattern", false), ("minor_illusion", true), ("silent_image", true) })
        {
            Assert.True(spells.TryGet(RulesetSystem.Dnd5e, name, out var spell));
            Assert.Equal(figment, IllusionRules.IsFigment(spell));
        }
    }

    [Fact]
    public void A_beat_keeps_its_witness_line_until_the_impression_step_has_reported_it()
    {
        var beats = new CampaignVault.Data.Context.PerceptibleBeats(null);
        var ev = new EventOccurred { Summary = "Maeve sings", InitiatorId = Maeve.Id, Perceptible = "loud", Appeal = "delight" };
        Assert.False(Assert.Single(beats.Detect([ev], RulesetSystem.Dnd5e)).HasAppeal);
        ev.ImpressionsReported = true;
        Assert.True(Assert.Single(beats.Detect([ev], RulesetSystem.Dnd5e)).HasAppeal);
    }

    // ---- I5: appraise ----

    private static Appraisal Appraise(Character observer, Appeal appeal, ImpressionRead read = ImpressionRead.Sincere, bool magical = false, int repeats = 0) =>
        ImpressionResolver.Appraise(observer, Maeve, appeal, new ReadResult(read), new AppraisalFacts(magical, repeats, false, 0));

    [Fact]
    public void Butterflies_enchant_a_child_please_an_adult_and_unsettle_the_superstitious()
    {
        var butterflies = AppealOf(new() { ["appeal"] = "delight", ["subject"] = "butterflies" });

        var child = Appraise(Person("chars/pip", stage: LifeStage.Child), butterflies, magical: true);
        var adult = Appraise(Person("chars/tam"), butterflies, magical: true);
        var elder = Appraise(Person("chars/wenna", stage: LifeStage.Elder, traits: ["superstitious"]), butterflies, magical: true);

        Assert.Equal(ImpressionTier.Mild, adult.Tier);
        Assert.True(child.Score > adult.Score);
        Assert.Contains("a child", child.Reasons);
        Assert.Equal("unease", elder.Tone.Name);
        Assert.Contains("distrusts magic", elder.Reasons);
        Assert.True(child.KnowsAuthor);
    }

    [Fact]
    public void A_feared_subject_frightens_and_a_wanted_one_doubles_down()
    {
        var moths = AppealOf(new() { ["appeal"] = "delight", ["subject"] = "a cloud of moths" });
        var phobic = Appraise(Person(fears: ["moths and other insects"]), moths);
        Assert.Equal("unease", phobic.Tone.Name);
        Assert.Contains("fears moths and other insects", phobic.Reasons);

        var lover = Appraise(Person(wants: ["to see moths again"]), moths);
        Assert.Equal("delight", lover.Tone.Name);
        Assert.Contains("loves to see moths again", lover.Reasons);
    }

    [Fact]
    public void Flattery_seen_through_is_an_insult()
    {
        var flattery = AppealOf(new() { ["appeal"] = "pride", ["deceptive"] = "true" }, RulesetActionType.SkillCheck, "Persuasion");
        var caught = Appraise(Person(), flattery, ImpressionRead.SawThrough);
        Assert.Equal("insult", caught.Tone.Name);
        Assert.True(ImpressionResolver.RelationshipDelta(caught) < 0);
        Assert.True(ImpressionResolver.RelationshipDelta(Appraise(Person(), flattery, ImpressionRead.Believed)) > 0);
    }

    [Fact]
    public void A_lone_shaken_scout_flees_and_the_fourth_fright_is_weaker_than_the_first()
    {
        var ghost = AppealOf(new() { ["appeal"] = "fear", ["intensity"] = "strong", ["deceptive"] = "true" });
        var scout = Person("chars/scout", morale: 25);

        var first = Appraise(scout, ghost, ImpressionRead.Believed, magical: true);
        var fourth = Appraise(scout, ghost, ImpressionRead.Believed, magical: true, repeats: 3);

        Assert.Equal("flees", first.Responds);
        Assert.Contains("morale low", first.Reasons);
        Assert.False(first.KnowsAuthor);
        Assert.Equal(0, ImpressionResolver.RelationshipDelta(first));
        Assert.True(fourth.Score < first.Score);
        Assert.Contains("seen it 3 times", fourth.Reasons);
    }

    [Fact]
    public void Brave_steadies_and_reception_overrides_the_tone()
    {
        var ghost = AppealOf(new() { ["appeal"] = "fear", ["reception"] = "chars/o:amusement" });
        Assert.True(Appraise(Person("chars/b", traits: ["brave"]), ghost).Score < Appraise(Person("chars/t", traits: ["timid"]), ghost).Score);
        var yours = Appraise(Person(), ghost);
        Assert.Equal("amusement", yours.Tone.Name);
        Assert.Contains("your call: amusement", yours.Reasons);
    }

    [Fact]
    public void A_seen_through_wonder_is_still_wonder_a_tier_less()
    {
        var lights = AppealOf(new() { ["appeal"] = "awe", ["intensity"] = "strong", ["deceptive"] = "true" });
        var knowing = Appraise(Person(), lights, ImpressionRead.SawThrough, magical: true);
        Assert.Equal("awe", knowing.Tone.Name);
        Assert.Equal(ImpressionTier.Mild, knowing.Tier);
    }

    [Fact]
    public void A_scout_alone_breaks_sooner_and_one_among_allies_holds_longer()
    {
        var ghost = AppealOf(new() { ["appeal"] = "fear", ["intensity"] = "strong" });
        Appraisal With(Company company) =>
            ImpressionResolver.Appraise(Person("chars/s"), Maeve, ghost, new ReadResult(ImpressionRead.Believed), new AppraisalFacts(true, 0, false, 0, company));

        var alone = With(Company.Alone);
        var unknown = With(Company.Unknown);
        var allies = With(Company.Accompanied);
        Assert.True(alone.Score > unknown.Score && unknown.Score > allies.Score);
        Assert.Contains("alone", alone.Reasons);
        Assert.Contains("among allies", allies.Reasons);
        Assert.Equal("flees", alone.Responds);
        Assert.Equal("retreats", unknown.Responds);
    }

    [Fact]
    public void Suspecting_the_caster_is_a_tier_less_convincing_and_names_the_author()
    {
        var ghost = AppealOf(new() { ["appeal"] = "fear", ["intensity"] = "strong", ["deceptive"] = "true" });
        var believed = Appraise(Person(), ghost, ImpressionRead.Believed, magical: true);
        var suspects = Appraise(Person(), ghost, ImpressionRead.Suspects, magical: true);
        Assert.False(believed.KnowsAuthor);
        Assert.True(suspects.KnowsAuthor);
        Assert.True(suspects.Score < believed.Score);
        Assert.True(ImpressionResolver.RelationshipDelta(suspects) < 0);
    }

    [Theory]
    [InlineData("8", "10", -1)]
    [InlineData("12", "10", 0)]
    [InlineData("20", "10", 1)]
    public void A_check_against_a_dc_moves_the_tier(string total, string dc, int shift) =>
        Assert.Equal(shift, ImpressionResolver.QualityShift(new RulesetAction
        {
            CharacterId = "chars/m", ActionName = "Performance", ActionType = RulesetActionType.SkillCheck,
            Parameters = new() { ["engineRollTotal"] = total, ["dc"] = dc }
        }));
}
