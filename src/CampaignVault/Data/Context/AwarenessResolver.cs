using CampaignVault.Models;

namespace CampaignVault.Data.Context;

public enum Noticed
{
    /// <summary>Saw or heard it, and who did it.</summary>
    Perceives,

    /// <summary>Perceived the effect but not its source (an invisible caster's fireball, an unseen speaker).</summary>
    EffectNotSource,

    /// <summary>Asleep, and this woke them.</summary>
    Wakes,

    Misses,
}

/// <summary>What one observer got from one signal: the outcome, the senses that carried it, and a short note for the narrator.</summary>
public sealed record Notice(Noticed Outcome, Perceptible Senses, string? Note = null)
{
    public bool Perceived => Outcome != Noticed.Misses;
}

/// <summary>
/// Who notices what (WITNESS_CHANNEL_PLAN.md A3), from the A0 rules audit. These are core rules shared by every creature, so code,
/// not data:
/// <list type="bullet">
/// <item>Unconscious, petrified and paralyzed creatures perceive nothing ("unaware of its surroundings", SRD 5.1).</item>
/// <item>Blinded can't see, deafened can't hear.</item>
/// <item>Asleep can't see. Loud wakes them (D6; Player Core: noise wakes a sleeper, simplified to automatic since the engine has no
/// noise DCs). A quiet source wakes them only if their passive Perception, −5 (5e house rule) or −4 (pf2e), meets its Stealth.</item>
/// <item>Distracted, or absorbed in this beat's action (D7): passive Perception −5, the SRD's disadvantage value.</item>
/// <item>A hidden source is noticed when passive Perception meets its Stealth total (SRD 5.1 Hiding).</item>
/// <item>An invisible source is heard but not seen ("location can be detected by any noise it makes").</item>
/// </list>
/// </summary>
public static class AwarenessResolver
{
    public const int DisadvantagePenalty = 5;
    public const int Pf2eAsleepPenalty = 4;

    /// <summary>
    /// How long a state lasts untouched (house rules, the rules texts give none): a night's sleep with margin, an hour of
    /// distraction, an hour of hiding without a fresh Stealth check. The dispatcher clears them after that.
    /// </summary>
    public const double MaxSleepHours = 10;
    public const double MaxDistractedHours = 1;
    public const double MaxHiddenHours = 1;

    private static readonly HashSet<string> Insensible = new(StringComparer.OrdinalIgnoreCase) { "unconscious", "petrified", "paralyzed" };

    /// <summary>Skills whose checks absorb the one making them for the beat (D7).</summary>
    private static readonly HashSet<string> AbsorbingSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "performance", "investigation", "search", "arcana", "history", "religion", "nature", "medicine", "crafting", "society", "occultism",
    };

    public static HashSet<string> Conditions(Character c) =>
        (c.SystemStats?.StatusEffects ?? []).Select(e => e.ConditionName ?? e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAsleep(Character c) => c.Awareness?.State == AwarenessState.Asleep;

    /// <summary>A spectacle (fireball, shout, song): who perceives it and through which senses.</summary>
    public static Notice Perceive(Character observer, Perceptible signal, Character? source = null)
    {
        var conditions = Conditions(observer);
        if (conditions.Overlaps(Insensible))
        {
            return new Notice(Noticed.Misses, Perceptible.None, "unconscious");
        }

        var asleep = IsAsleep(observer);
        var senses = signal;
        if (asleep || conditions.Contains("blinded"))
        {
            senses &= ~Perceptible.Visible;
        }

        if (conditions.Contains("deafened"))
        {
            senses &= ~Perceptible.Loud;
        }

        if (senses == Perceptible.None)
        {
            return new Notice(Noticed.Misses, Perceptible.None, asleep ? "asleep" : null);
        }

        if (asleep)
        {
            return new Notice(Noticed.Wakes, senses, "woken by it");
        }

        if (source != null && Conditions(source).Contains("invisible"))
        {
            return new Notice(Noticed.EffectNotSource, senses, "can't see who did it");
        }

        var note = senses == signal || signal != Perceptible.Both
            ? null
            : senses == Perceptible.Loud ? "blinded: heard it only" : "deafened: saw it only";
        return new Notice(Noticed.Perceives, senses, note);
    }

    /// <summary>Passive Perception after sleep or distraction: −5 (pf2e asleep −4).</summary>
    public static int PassivePerception(Character observer, bool absorbed = false, string? system = null)
    {
        var score = ContextTurnReads.PassivePerception(observer);
        if (IsAsleep(observer))
        {
            return score - (string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase) ? Pf2eAsleepPenalty : DisadvantagePenalty);
        }

        return absorbed || observer.Awareness?.State == AwarenessState.Distracted ? score - DisadvantagePenalty : score;
    }

    /// <summary>
    /// A quiet or hidden presence (an intruder at night, a pickpocket, an invisible sneak): whether this observer notices the source
    /// itself. A source in plain view is noticed by anyone awake who can see it; a hiding one only when passive Perception meets its
    /// Stealth total.
    /// </summary>
    public static Notice NoticePresence(Character observer, Character source, bool absorbed = false, string? system = null)
    {
        var conditions = Conditions(observer);
        if (conditions.Overlaps(Insensible))
        {
            return new Notice(Noticed.Misses, Perceptible.None, "unconscious");
        }

        var asleep = IsAsleep(observer);
        var invisible = Conditions(source).Contains("invisible");
        var hidden = source.Concealment?.StealthTotal;
        var canSee = !asleep && !conditions.Contains("blinded") && !invisible;
        if (hidden == null && canSee)
        {
            return new Notice(Noticed.Perceives, Perceptible.Visible);
        }

        // Hiding, invisible, or an observer who can't see: only a passive Perception check against the source's quiet catches it.
        var score = PassivePerception(observer, absorbed, system);
        var stealth = hidden ?? 10;
        if (conditions.Contains("deafened") && !canSee)
        {
            return new Notice(Noticed.Misses, Perceptible.None, $"passive Perception {score} vs Stealth {stealth}, can't see or hear");
        }

        var note = $"passive Perception {score} vs Stealth {stealth}";
        if (score < stealth)
        {
            return new Notice(Noticed.Misses, Perceptible.None, note);
        }

        return asleep ? new Notice(Noticed.Wakes, Perceptible.Loud, note)
            : canSee ? new Notice(Noticed.Perceives, Perceptible.Visible, note)
            : new Notice(Noticed.EffectNotSource, Perceptible.Loud, note + "; hears them but can't see them");
    }

    /// <summary>Who this batch absorbs for the beat (D7): conversation partners, absorbing skill checks, and anything marked absorbed.</summary>
    public static HashSet<string> AbsorbedIds(IEnumerable<WorldChange> changes)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            switch (change)
            {
                case EventOccurred { Category: EventCategory.Conversation } talk:
                    ids.UnionWith(talk.Involved ?? []);
                    break;
                case RulesetAction action when string.Equals(action.Parameters.GetValueOrDefault("absorbed")?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                                               || (action.ActionType is RulesetActionType.SkillCheck
                                                   && AbsorbingSkills.Contains(action.Parameters.GetValueOrDefault("skill")?.Trim() ?? "")):
                    ids.Add(action.CharacterId);
                    break;
            }
        }

        return ids;
    }
}
