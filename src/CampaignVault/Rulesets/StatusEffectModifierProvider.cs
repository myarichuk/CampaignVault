using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// The numeric layer: sums <see cref="StatusEffect.StatModifiers"/> for a roll. It replaces the private fold the resolvers
/// used to carry, with the same arithmetic (see the characterization tests) except that keys now match ignoring case, spaces,
/// underscores and hyphens, so "Sleight of Hand", "sleight_of_hand" and "SleightOfHand" are one skill.
/// Situational advantage is not its business; providers add that.
/// </summary>
internal sealed class StatusEffectModifierProvider : IRollModifierProvider
{
    public IEnumerable<RollModifier> Modifiers(RollQuery query)
    {
        var bonus = Sum(query.Actor.SystemStats?.StatusEffects, query.Kind, query.Subject);
        return bonus == 0 ? [] : [new RollModifier("status effects", bonus, AdvantageEffect.None, "")];
    }

    /// <summary>The floored sum of every active effect's modifiers that apply to this kind of roll and subject.</summary>
    public static int Sum(IEnumerable<StatusEffect>? effects, string kind, string? subject)
    {
        if (effects is null)
            return 0;

        var tags = TagsFor(kind, subject);
        var allChecks = kind == RollKinds.Check;
        var allSaves = kind == RollKinds.Save;
        var allRolls = kind is not (RollKinds.ArmorClass or RollKinds.Speed);
        var bonus = 0f;
        foreach (var effect in effects)
        {
            if (effect.StatModifiers is not { Count: > 0 } mods)
                continue;

            foreach (var tag in tags)
                bonus += Find(mods, tag);
            if (allRolls)
                bonus += Find(mods, "AllRolls");
            if (allChecks)
                bonus += Find(mods, "AllChecks");
            if (allSaves)
                bonus += Find(mods, "AllSaves");
        }

        return (int)Math.Floor(bonus);
    }

    /// <summary>Lower-case, letters and digits only: the shape both sides of a key comparison are reduced to.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static IEnumerable<string> TagsFor(string kind, string? subject)
    {
        switch (kind)
        {
            case RollKinds.Attack: return ["AttackRoll"];
            case RollKinds.Damage: return ["DamageRoll"];
            case RollKinds.ArmorClass: return ["AC"];
            case RollKinds.Initiative: return ["Initiative"];
            case RollKinds.Speed: return ["Speed"];
            case RollKinds.Check: return subject is null ? ["SkillCheck"] : ["SkillCheck", subject];
            case RollKinds.Save: return subject is null ? ["SavingThrow"] : ["SavingThrow", subject];
            default: return [];
        }
    }

    private static float Find(Dictionary<string, float> mods, string tag)
    {
        var wanted = Normalize(tag);
        var total = 0f;
        foreach (var (key, value) in mods)
        {
            if (Normalize(key) == wanted)
                total += value;
        }

        return total;
    }
}
