using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>The closed vocabulary of numeric things a feat effect can do to a roll.</summary>
public static class FeatEffectKinds
{
    public const string AttackBonus = "attackBonus";
    public const string DamageBonus = "damageBonus";
    public const string SkillBonus = "skillBonus";
    public const string SaveBonus = "saveBonus";
    public const string ArmorClassBonus = "armorClassBonus";

    /// <summary>Advantage / disadvantage on the rolls named by <c>on</c> (attack, check or save). Carries no value.</summary>
    public const string Advantage = "advantage";
    public const string Disadvantage = "disadvantage";

    /// <summary>Extra damage dice (<c>dice</c>, <c>damageType</c>) on a hit; rolled again on a critical hit. 5e.</summary>
    public const string ExtraDamage = "extraDamage";

    /// <summary>The lowest natural d20 that is a critical hit (19 widens the range to 19-20). 5e.</summary>
    public const string CritRange = "critRange";

    /// <summary>
    /// Resistance to the damage type (<c>damageType</c>, or several comma-separated): damage of that type to the character is
    /// halved. 5e.
    /// </summary>
    public const string Resistance = "resistance";

    /// <summary>Added to the character's initiative rolls.</summary>
    public const string InitiativeBonus = "initiativeBonus";

    /// <summary>Feet added to the character's speed.</summary>
    public const string SpeedBonus = "speedBonus";

    /// <summary>Added to a passive score: <c>subject</c> Perception (the default) or Investigation. 5e.</summary>
    public const string PassiveBonus = "passiveBonus";

    /// <summary>
    /// Damage to the character is reduced by <c>value</c> (before resistance), only damage of <c>damageType</c> when set (or
    /// several comma-separated). 5e attacks.
    /// </summary>
    public const string DamageReduction = "damageReduction";

    public static readonly IReadOnlyList<string> All =
    [
        AttackBonus, DamageBonus, SkillBonus, SaveBonus, ArmorClassBonus, Advantage, Disadvantage, ExtraDamage, CritRange, Resistance,
        InitiativeBonus, SpeedBonus, PassiveBonus, DamageReduction,
    ];

    /// <summary>Kinds that carry no number: their weight is in <c>dice</c>, <c>damageType</c> or nothing at all.</summary>
    public static readonly IReadOnlyList<string> Valueless = [Advantage, Disadvantage, ExtraDamage, Resistance];
}

/// <summary>PF2e bonus types. Among a feat's effects only the highest bonus and the worst penalty of each typed kind count.</summary>
public static class FeatBonusTypes
{
    public const string Untyped = "untyped";
    public const string Circumstance = "circumstance";
    public const string Item = "item";
    public const string Status = "status";

    public static readonly IReadOnlyList<string> All = [Untyped, Circumstance, Item, Status];
}

/// <summary>
/// Gate on a feat or one of its effects: the plugin must be loaded and, when <see cref="Mode"/> is set, that interaction
/// mode must be running. <see cref="Plugin"/> alone means "any mode of that plugin, or none"; <see cref="Mode"/> alone
/// means "that mode, whichever plugin owns it".
/// </summary>
public sealed class FeatRequirement
{
    [Description("Plugin id that must be loaded (plugin.json id). Omit to gate on the mode alone.")]
    [JsonPropertyName("plugin")]
    public string? Plugin { get; set; }

    [Description("Interaction mode id that must currently be running. Omit to require only the plugin.")]
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

/// <summary>
/// One declarative effect of a feat. The engine owns the number (<see cref="Value"/>); a model only ever supplies a fact:
/// a toggle it chose (<see cref="Toggle"/>) or a condition it asserts holds (<see cref="Assert"/>).
/// </summary>
public sealed class FeatEffect
{
    [Description("attackBonus, damageBonus, skillBonus, saveBonus, armorClassBonus, advantage, disadvantage, extraDamage, critRange or resistance.")]
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [Description("The modifier (negative for a penalty). Fixed in the data; the DM never supplies it.")]
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [Description("PF2e only: untyped (default), circumstance, item or status. Among a character's feat effects only the highest bonus and worst penalty of each typed kind apply.")]
    [JsonPropertyName("bonusType")]
    public string? BonusType { get; set; }

    [Description("Skill (skillBonus), save (saveBonus) or ability the effect is limited to, e.g. 'stealth'. Omit for all.")]
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [Description("advantage / disadvantage: which rolls it applies to - attack, check or save ('subject' narrows a check or save to one skill or ability).")]
    [JsonPropertyName("on")]
    public string? On { get; set; }

    [Description("extraDamage: the dice added on a hit, e.g. '1d8' (rolled again on a critical hit).")]
    [JsonPropertyName("dice")]
    public string? Dice { get; set; }

    [Description("extraDamage / resistance: the damage type, e.g. 'radiant' (extraDamage may omit it).")]
    [JsonPropertyName("damageType")]
    public string? DamageType { get; set; }

    [Description("Engine-checked weapon conditions, all required: ranged, melee, finesse, twoHanded, heavy. Read from the weapon's tags or the action category.")]
    [JsonPropertyName("weapon")]
    public List<string> Weapon { get; set; } = [];

    [Description("Name of an action parameter the player must set true for this effect to apply (an opt-in such as 'powerAttack').")]
    [JsonPropertyName("toggle")]
    public string? Toggle { get; set; }

    [Description("DM-judged condition flags, all required in the action's 'assert' parameter (comma-separated), e.g. 'allyNear'. The DM claims the condition holds; the engine still applies the fixed value.")]
    [JsonPropertyName("assert")]
    public List<string> Assert { get; set; } = [];

    [Description("Plain-language condition shown to the DM for asserted effects, e.g. 'an ally is adjacent to the target'.")]
    [JsonPropertyName("when")]
    public string? When { get; set; }

    [Description("Optional gate on this effect alone (see the feat-level 'requires').")]
    [JsonPropertyName("requires")]
    public FeatRequirement? Requires { get; set; }

    [JsonIgnore]
    public bool NeedsAssertion => Assert.Count > 0;
}

/// <summary>A feat effect that is live for a character this action, with the feat it came from (for the roll notes).</summary>
public sealed record ActiveFeatEffect(string FeatName, FeatEffect Effect);
