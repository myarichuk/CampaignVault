using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>Whether something gives advantage or disadvantage on a roll. Any advantage and any disadvantage cancel.</summary>
public enum AdvantageEffect
{
    None,
    Advantage,
    Disadvantage,
}

/// <summary>The kinds of roll a <see cref="RollQuery"/> can be about.</summary>
public static class RollKinds
{
    public const string Attack = "attack";
    public const string Damage = "damage";
    public const string ArmorClass = "ac";
    public const string Check = "check";
    public const string Save = "save";
    public const string Initiative = "initiative";

    /// <summary>Movement in feet per round; the "bonus" is feet, not a d20 modifier.</summary>
    public const string Speed = "speed";
}

/// <summary>
/// What a roll is about, handed to every <see cref="IRollModifierProvider"/>. <see cref="Subject"/> is the normalized skill or
/// save name ("athletics", "wisdom"); <see cref="Tags"/> say what the roll is against ("charm", "fear", "compulsion", "poison",
/// "mental"), empty when nothing was stated. <see cref="Actor"/> is whoever rolls (the defender for a save); <see cref="Other"/>
/// is the opponent when there is one. <see cref="Options"/> is the campaign's system options, already loaded, so a synchronous
/// provider can read settings.
/// </summary>
public sealed record RollQuery(
    string Kind,
    string? Subject,
    IReadOnlyCollection<string> Tags,
    Character Actor,
    Character? Other,
    string? System,
    IReadOnlyDictionary<string, string> Options);

/// <summary>One provider's say on a roll: a numeric bonus and/or advantage, and a reason the player will read.</summary>
public sealed record RollModifier(string Source, int Bonus, AdvantageEffect Advantage, string Reason);

/// <summary>What the pipeline made of a roll: the final bonus, the net advantage, and the reasons in order.</summary>
public sealed record RollResolution(int Bonus, AdvantageEffect Advantage, IReadOnlyList<string> Notes)
{
    public static RollResolution Unchanged(int bonus, AdvantageEffect advantage = AdvantageEffect.None) => new(bonus, advantage, []);
}

/// <summary>
/// Plugin hook for what buffs, debuffs, conditions and stats do to rolls (SDK 0.11.0). Registered by convention like handlers
/// and observers, and only for the campaign's active system. Core asks every provider for each attack, save, check,
/// initiative and speed, folds the numeric bonuses, cancels advantage against disadvantage, and prints each reason.
///
/// Must be pure, synchronous and cheap: it runs inside a roll and cannot mutate anything. A provider that throws is logged and
/// skipped; it never fails the roll. Rule: numbers that expire live on a <see cref="StatusEffect"/> (core folds those itself);
/// providers add situational advantage/disadvantage and tag-specific bonuses. Never stamp an effect and also return the same
/// number here, or it counts twice.
/// </summary>
public interface IRollModifierProvider
{
    IEnumerable<RollModifier> Modifiers(RollQuery query);
}
