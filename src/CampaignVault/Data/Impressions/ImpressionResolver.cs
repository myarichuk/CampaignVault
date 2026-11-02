using CampaignVault.Data.Context;
using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Impressions;

/// <summary>What the batch knows about the beat that the reading needs (IMPRESSIONS_PLAN.md I4).</summary>
public sealed record BeatFacts(
    bool IsIllusionSpell,
    int? RollTotal,
    bool? ActorWon,
    IReadOnlySet<string> ContestTargets,
    int? DisbeliefDc,
    string? System);

/// <summary>Whether the observer bought it, and the numbers that said so.</summary>
public sealed record ReadResult(ImpressionRead Read, string? Note = null);

/// <summary>How it landed on one observer, and what they're inclined to do about it (IMPRESSIONS_PLAN.md I5).</summary>
public sealed record Appraisal(Tone Tone, ImpressionTier Tier, double Score, IReadOnlyList<string> Reasons, string Responds, bool KnowsAuthor);

/// <summary>Whether the observer has anyone at their side (IMPRESSIONS_PLAN.md F2). Unknown moves nothing.</summary>
public enum Company
{
    Unknown,
    Alone,
    Accompanied,
}

/// <summary>What the appraisal needs beyond the observer's own sheet.</summary>
public sealed record AppraisalFacts(bool Magical, int Repeats, bool Unseen, int QualityShift, Company Company = Company.Unknown);

/// <summary>
/// One observer, one beat: Read, then Appraise (IMPRESSIONS_PLAN.md I4, I5). Pure: everything it needs is passed in, so the
/// dispatcher's step and the tests see the same answer.
/// </summary>
public static class ImpressionResolver
{
    /// <summary>
    /// Passive Insight: <c>Attributes["passiveInsight"]</c> when recorded; 5e 10 + Insight (or the Wisdom modifier, untrained);
    /// PF2e the Perception DC, which is what Sense Motive sets against Deception (Player Core).
    /// </summary>
    public static int PassiveInsight(Character observer)
    {
        if (observer.SystemStats?.Attributes?.TryGetValue("passiveInsight", out var recorded) == true && recorded > 0)
        {
            return (int)Math.Round(recorded);
        }

        return observer.SystemStats switch
        {
            Dnd5eExtension five => 10 + (Skill(five.SkillModifiers, "Insight") ?? five.GetAbilityModifier(five.Wisdom)),
            Pf2eExtension => ContextTurnReads.PassivePerception(observer),
            _ => 10,
        };
    }

    /// <summary>The deceiver's passive Deception (10 + Deception, or the Charisma modifier untrained), for a lie told with no roll.</summary>
    public static int PassiveDeception(Character actor) => actor.SystemStats switch
    {
        Dnd5eExtension five => 10 + (Skill(five.SkillModifiers, "Deception") ?? five.GetAbilityModifier(five.Charisma)),
        Pf2eExtension pf2 => 10 + (Skill(pf2.SkillModifiers, "Deception") ?? pf2.CharismaMod),
        _ => 10,
    };

    /// <summary>A skill's modifier from the sheet, or, untrained, the Dexterity (or Charisma) modifier it falls back to.</summary>
    public static int SkillBonus(Character c, string skill, bool dexterity) => c.SystemStats switch
    {
        Dnd5eExtension five => Skill(five.SkillModifiers, skill) ?? five.GetAbilityModifier(dexterity ? five.Dexterity : five.Charisma),
        Pf2eExtension pf2 => Skill(pf2.SkillModifiers, skill) ?? (dexterity ? pf2.DexterityMod : pf2.CharismaMod),
        _ => 0,
    };

    private static int? Skill(Dictionary<string, int>? skills, string name) =>
        skills?.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : null;

    /// <summary>
    /// I4. Sincere beats land. An illusion is believed: the rules give a check only to a creature that spends its action
    /// examining it (the disbelieve check, I6). A contest's target follows the contest. Anyone else sets passive Insight against
    /// the roll, or against the actor's passive Deception when nothing was rolled; meeting it sees through (as passive Perception
    /// meets Stealth, SRD 5.1).
    /// </summary>
    public static ReadResult Read(Character observer, Character? actor, Appeal appeal, BeatFacts facts)
    {
        if (!appeal.Deceptive)
        {
            return new ReadResult(ImpressionRead.Sincere);
        }

        if (facts.IsIllusionSpell)
        {
            var dc = facts.DisbeliefDc?.ToString() ?? "the spell DC";
            return new ReadResult(ImpressionRead.Believed,
                $"believes it; examining it takes an action: {IllusionRules.DisbeliefSkill(facts.System)} vs DC {dc}");
        }

        if (facts.ActorWon is { } won && facts.ContestTargets.Contains(observer.Id))
        {
            return won
                ? new ReadResult(ImpressionRead.Believed, "believes it (lost the contest)")
                : new ReadResult(ImpressionRead.SawThrough, "saw through it (won the contest)");
        }

        // With no roll, the deceiver stands at their average result, 10.5 + Deception: an Insight of 11 + Deception meets it. Ties
        // going to the observer against a flat 10 would have every untrained listener see through every untrained liar.
        var insight = PassiveInsight(observer);
        var rolled = facts.RollTotal is not null;
        var against = facts.RollTotal ?? (actor is null ? 11 : PassiveDeception(actor) + 1);
        var basis = rolled ? "" : " (no roll: the liar's average)";
        return insight >= against
            ? new ReadResult(ImpressionRead.SawThrough, $"saw through it: Insight {insight} vs {against}{basis}")
            : new ReadResult(ImpressionRead.Believed, $"believes it: Insight {insight} vs {against}{basis}");
    }

    private static readonly string[] Superstitious = ["superstitious", "distrusts magic", "distrust of magic", "fears magic", "hates magic", "witch", "zealot"];
    private static readonly string[] Brave = ["brave", "fearless", "bold", "stoic", "unflappable"];
    private static readonly string[] Timid = ["coward", "timid", "skittish", "nervous", "fearful", "jumpy"];
    private static readonly string[] Jaded = ["jaded", "cynical", "dour", "grim", "world-weary"];
    private static readonly string[] Whimsical = ["cheerful", "whimsical", "curious", "dreamy", "romantic", "playful"];
    private static readonly string[] Vain = ["vain", "proud", "arrogant", "pompous"];

    /// <summary>
    /// I5. The tone (the narrator's <c>reception</c> for this observer wins, a deception seen through turns, a superstitious soul
    /// is uneasy at any magic, a feared subject frightens), then how strongly: intensity, moved by the check's quality, by who they
    /// are (life stage, Openness for wonder, Resilience and morale and wounds for fright, temperament keywords in Traits), by their
    /// opinion of the actor, and worn down by how often they've had it from them in the last day (÷ 1 + repeats/2).
    /// </summary>
    public static Appraisal Appraise(Character observer, Character? actor, Appeal appeal, ReadResult read, AppraisalFacts facts)
    {
        var reasons = new List<string>();
        var tone = appeal.Tone;
        if (appeal.Reception.TryGetValue(observer.Id, out var overridden) && Tones.TryGet(overridden, out var yours))
        {
            tone = yours;
            reasons.Add($"your call: {yours.Name}");
        }

        var level = (int)appeal.Intensity + facts.QualityShift;
        if (facts.QualityShift < 0)
        {
            reasons.Add("it fell flat");
        }
        else if (facts.QualityShift > 0)
        {
            reasons.Add("superbly done");
        }

        // F3: they saw it cast. It still might be real (a summoning, a conjured beast), but it's less convincing.
        if (read.Read == ImpressionRead.Suspects)
        {
            level--;
        }

        if (read.Read == ImpressionRead.SawThrough)
        {
            var turned = Tones.SeenThrough(tone);
            if (turned == tone)
            {
                level--;
            }

            tone = turned;
        }

        var psyche = observer.Psychology;
        var temperament = string.Join(" | ", (psyche?.Traits ?? []).Concat(psyche?.Fears ?? [])).ToLowerInvariant();
        if (facts.Magical && tone.Valence > 0 && read.Read != ImpressionRead.SawThrough && Superstitious.Any(temperament.Contains))
        {
            tone = Tones.Get("unease");
            reasons.Add("distrusts magic");
        }

        var multiplier = 1.0;
        if (Match(appeal.Subject, psyche?.Fears) is { } feared)
        {
            if (!tone.Fright)
            {
                tone = tone.Valence > 0 ? Tones.Get("unease") : Tones.Get("fear");
            }

            multiplier *= 1.5;
            reasons.Add($"fears {feared}");
        }
        else if (tone.Valence > 0 && Match(appeal.Subject, psyche?.Wants) is { } wanted)
        {
            multiplier *= 1.5;
            reasons.Add($"loves {wanted}");
        }

        switch (observer.LifeStage)
        {
            case LifeStage.Child when tone.Wonder || tone.Fright:
                multiplier *= 1.5;
                reasons.Add("a child");
                break;
            case LifeStage.Adolescent when tone.Wonder || tone.Fright:
                multiplier *= 1.2;
                break;
            case LifeStage.Elder when tone.Wonder:
                multiplier *= 0.75;
                reasons.Add("seen it all");
                break;
        }

        var openness = psyche?.Openness ?? 0.5;
        var resilience = psyche?.Resilience ?? 0.5;
        if (tone.Wonder)
        {
            multiplier *= 0.5 + openness;
            if (Whimsical.Any(temperament.Contains))
            {
                multiplier *= 1.3;
            }
        }

        if (tone.Valence > 0 && Jaded.Any(temperament.Contains))
        {
            multiplier *= 0.6;
            reasons.Add("jaded");
        }

        if (tone.Name == "pride" && Vain.Any(temperament.Contains))
        {
            multiplier *= 1.5;
            reasons.Add("vain");
        }

        var morale = observer.SystemStats?.Morale ?? 65f;
        var hurt = observer.MaxHp > 0 && observer.CurrentHp * 2 < observer.MaxHp;
        if (tone.Fright)
        {
            multiplier *= 1.5 - resilience;
            multiplier *= 1 + (50 - morale) / 100.0;
            if (Brave.Any(temperament.Contains))
            {
                multiplier *= 0.5;
                reasons.Add("brave");
            }
            else if (Timid.Any(temperament.Contains))
            {
                multiplier *= 1.5;
                reasons.Add("timid");
            }

            if (morale < 35)
            {
                reasons.Add("morale low");
            }

            if (hurt)
            {
                multiplier *= 1.25;
                reasons.Add("hurt");
            }

            switch (facts.Company)
            {
                case Company.Alone:
                    multiplier *= 1.3;
                    reasons.Add("alone");
                    break;
                case Company.Accompanied:
                    multiplier *= 0.85;
                    reasons.Add("among allies");
                    break;
            }
        }

        var knowsAuthor = actor != null && !facts.Unseen && !(read.Read == ImpressionRead.Believed && facts.Magical && appeal.Deceptive);
        if (knowsAuthor && tone.Valence > 0 && actor != null)
        {
            var opinion = Opinion(observer, actor.Id);
            multiplier *= Math.Clamp(1 + opinion / 200.0, 0.5, 1.5);
            if (opinion >= 30)
            {
                reasons.Add($"fond of {actor.Name}");
            }
            else if (opinion <= -30)
            {
                reasons.Add($"dislikes {actor.Name}");
            }
        }

        if (facts.Repeats > 0)
        {
            // Habituation: the second time counts two thirds, the third half, the fifth a third.
            multiplier /= 1 + facts.Repeats / 2.0;
            reasons.Add(facts.Repeats == 1 ? "seen it before" : $"seen it {facts.Repeats} times");
        }

        var score = Math.Max(0, level) * multiplier;
        var tier = score switch
        {
            < 0.6 => ImpressionTier.Faint,
            < 1.5 => ImpressionTier.Mild,
            < 2.5 => ImpressionTier.Strong,
            _ => ImpressionTier.Overwhelming,
        };

        // D6/F2: a strong fright breaks a shaken, wounded or lone creature: what's a retreat for the steady is flight for them.
        var responds = tone.Fright && tier == ImpressionTier.Strong && (morale < 35 || hurt || facts.Company == Company.Alone)
            ? tone.Responds(ImpressionTier.Overwhelming)
            : tone.Responds(tier);
        return new Appraisal(tone, tier, Math.Round(score, 2), reasons, responds, knowsAuthor);
    }

    /// <summary>The observer's recorded opinion of the actor (case-insensitive key), 0 when none.</summary>
    public static int Opinion(Character observer, string actorId) =>
        observer.Social?.Relationships?.FirstOrDefault(kv => string.Equals(kv.Key, actorId, StringComparison.OrdinalIgnoreCase)).Value ?? 0;

    /// <summary>D1: a beat that was a check against a DC says how well it was done: failing drops a tier, beating it by 10 adds one.</summary>
    public static int QualityShift(RulesetAction? action)
    {
        if (action is null
            || !int.TryParse(action.Parameters.GetValueOrDefault(NoticingContextContributor.RollTotalKey), out var total)
            || !int.TryParse(action.Parameters.GetValueOrDefault("dc")?.Trim(), out var dc))
        {
            return 0;
        }

        return total < dc ? -1 : total >= dc + 10 ? 1 : 0;
    }

    /// <summary>A word of the subject (4+ letters, plural folded) found in one of the entries, or null.</summary>
    private static string? Match(string? subject, IEnumerable<string>? entries)
    {
        if (string.IsNullOrWhiteSpace(subject) || entries is null)
        {
            return null;
        }

        var words = subject.ToLowerInvariant()
            .Split([' ', ',', '.', ';', '-', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(w => w.TrimEnd('s'))
            .Where(w => w.Length >= 3)
            .ToList();
        foreach (var entry in entries.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var lower = entry.ToLowerInvariant();
            if (words.FirstOrDefault(lower.Contains) is { } hit)
            {
                return entry.Trim();
            }
        }

        return null;
    }

    /// <summary>The relationship change an impression earns toward its author (D4): only when they know who did it.</summary>
    public static int RelationshipDelta(Appraisal appraisal) =>
        !appraisal.KnowsAuthor || appraisal.Tone.Valence == 0 ? 0
        : appraisal.Tone.Valence * appraisal.Tier switch
        {
            ImpressionTier.Mild => 2,
            ImpressionTier.Strong => 4,
            ImpressionTier.Overwhelming => 6,
            _ => 0,
        };
}
