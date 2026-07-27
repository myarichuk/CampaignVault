using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// The active TTRPG ruleset for this campaign. Controls which IRulesetResolver
/// is selected by the RulesetActionHandler.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RulesetSystem
{
    Dnd5e,
    Pathfinder2e,
    Narrative
}

/// <summary>
/// The kind of action being requested. Used by IRulesetResolver to dispatch
/// to the correct resolution path (attack math, skill roll, pool roll, recovery, etc.).
/// Enums prevent LLM drift — the LLM must pick one of these exact values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RulesetActionType
{
    /// <summary>Standard attack — melee or ranged.</summary>
    Attack,

    /// <summary>Spell or ability that deals damage, applies a condition, or creates an area effect.</summary>
    Spell,

    /// <summary>Single-actor check against a static DC (e.g. Athletics DC 15).</summary>
    SkillCheck,

    /// <summary>Both actor and target roll; the higher success total wins.</summary>
    ContestedCheck,

    /// <summary>Actor rolls against target's static defence value (e.g. PF2e AC).</summary>
    OpposedCheck,

    /// <summary>Using a consumable or equipment item (e.g. Stimpak, potion, med-kit).</summary>
    UseItem,

    /// <summary>Medical or rest-based recovery action (First Aid, Treat Wounds, Bandage).</summary>
    Recovery,

    /// <summary>Target rolls to resist an effect (D&D/PF2e Saving Throw).</summary>
    SavingThrow
}

/// <summary>
/// Status of D&D-style advantage/disadvantage for an action.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdvantageState
{
    None,
    Advantage,
    Disadvantage
}

/// <summary>
/// Broad category of the action being performed. Lets resolvers and context views
/// understand the nature of an action without parsing ActionName free-text.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionCategory
{
    Melee,
    Ranged,
    Spell,
    /// <summary>Trip, grapple, shove, disarm, feint, and similar tactical moves.</summary>
    Maneuver,
    Social,
    Survival
}

/// <summary>
/// Body part targeted by a localized attack or injury.
/// Used by systems with hit-location mechanics and for structured StatusEffect debuffs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BodyPart
{
    Head,
    /// <summary>Throat shots, choking, strangling, called-shot collar hits.</summary>
    Neck,
    Torso,
    LeftArm,
    RightArm,
    /// <summary>Weapon hand: grip loss, somatic spell components, disarm scenarios.</summary>
    LeftHand,
    /// <summary>Weapon hand: grip loss, somatic spell components, disarm scenarios.</summary>
    RightHand,
    LeftLeg,
    RightLeg,
    /// <summary>Pinning arrows/bolts/spikes, movement lock — painful to walk.</summary>
    LeftFoot,
    /// <summary>Pinning arrows/bolts/spikes, movement lock — painful to walk.</summary>
    RightFoot
}

/// <summary>
/// Dice rolling mechanic to apply when evaluating a RollRequest.
/// Enum prevents the LLM from free-texting an unrecognized mechanic name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiceMechanic
{
    /// <summary>Roll once, take result. e.g. "1d20+5".</summary>
    Standard,

    /// <summary>Roll twice, keep highest (D&D 5e Advantage).</summary>
    Advantage,

    /// <summary>Roll twice, keep lowest (D&D 5e Disadvantage).</summary>
    Disadvantage,

    /// <summary>
    /// Re-roll and add when the result equals the die's maximum face.
    /// Continues until a non-max result is rolled. Used by Shadowrun edge dice,
    /// some OSR systems, and Warhammer FRPG.
    /// </summary>
    Explosive,

    /// <summary>Roll NdX dice, keep the K highest. e.g. 4d6 drop lowest for D&D stat generation.</summary>
    KeepHighest,

    /// <summary>Roll NdX dice, keep the K lowest.</summary>
    KeepLowest,

    /// <summary>Roll succeeds if result &lt;= TargetNumber. Used by Basic Role-Play and old-school systems.</summary>
    RollUnder
}

/// <summary>
/// When a resource pool recovers. Used by ResourcePoolTemplate and ResourcePool.
/// Recovery types form a hierarchy: LongRest ⊃ ShortRest ⊃ PerTurn (each includes the ones below).
/// Daily and Never are independent (not part of the hierarchy).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecoveryType
{
    /// <summary>Recovers after 8 hours of long rest (D&D spell slots, PF2e spell levels). Includes ShortRest and PerTurn recovery.</summary>
    LongRest,

    /// <summary>Recovers after 1 hour of short rest (D&D Warlocks, PF2e Focus Points). Includes PerTurn recovery.</summary>
    ShortRest,

    /// <summary>Recovers at the start of each combat turn. LLM manually resets via resource commits.</summary>
    PerTurn,

    /// <summary>Recovers at the end of an encounter (rarely used; most systems use PerTurn or ShortRest instead).</summary>
    EncounterEnd,

    /// <summary>Resets daily at a configured time (Inspiration, daily uses of abilities). Independent of rest hierarchy.</summary>
    Daily,

    /// <summary>Never recovers — must be spent carefully or manually restored.</summary>
    Never
}

/// <summary>
/// Type of rest taken by a character (used to determine which resource pools recover).
/// Hierarchy: LongRest ⊃ ShortRest ⊃ PerTurn.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestType
{
    /// <summary>8+ hours of uninterrupted rest. Recovers LongRest, ShortRest, and PerTurn pools.</summary>
    LongRest,

    /// <summary>1-8 hours of rest. Recovers ShortRest and PerTurn pools (not LongRest).</summary>
    ShortRest,

    /// <summary>Combat turn start. Recovers only PerTurn pools.</summary>
    PerTurn
}
