namespace CampaignVault.Data.Templates;

/// <summary>dnd5e only. What happens to the immediate damage instance on a missed attack roll.</summary>
public enum MissBehavior
{
    None,
    Half,
}

/// <summary>dnd5e only. When a delayed damage tick fires.</summary>
public enum DelayedTickTrigger
{
    EndOfTargetNextTurn,
}

/// <summary>
/// dnd5e only. Upcast bonus dice the caster assigns to one pool of their choice
/// (Flame Strike's "the fire damage or the radiant damage (your choice) increases
/// by 1d6 for each slot level above 5th"). Eligible pools are the <see
/// cref="SpellDefinition.DamagePools"/> keys; the cast names one via the action's
/// <c>upcastPool</c> parameter. Dice are per slot above the spell's base slot.
/// </summary>
public record UpcastPoolChoice
{
    public string? BonusDicePerSlot { get; init; }
}

/// <summary>
/// dnd5e only. A second damage application scheduled for a later turn boundary
/// (Acid Arrow's "2d4 at the end of its next turn", scaling per slot).
/// </summary>
public record DelayedDamageTick
{
    public Dictionary<int, string>? DiceExpressionAtSlotLevel { get; init; }
    public string? DamageType { get; init; }
    public DelayedTickTrigger TriggerAt { get; init; } = DelayedTickTrigger.EndOfTargetNextTurn;
    public bool RequiresInitialHit { get; init; } = true;
}

public record SpellDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;

    /// <summary>0 = cantrip.</summary>
    public int? Level { get; init; }

    public List<string> Classes { get; init; } = [];
    public bool? Concentration { get; init; }
    public string? CastingTime { get; init; }

    /// <summary>Requires speech. dnd5e: literal Verbal component. pf2e: default true (Cast a Spell implies an incantation) unless the spell explicitly says otherwise.</summary>
    public bool? Verbal { get; init; }

    /// <summary>Requires a free hand / gesture. dnd5e: literal Somatic component. pf2e: spell has the Manipulate trait.</summary>
    public bool? Somatic { get; init; }

    /// <summary>Requires a physical material component or focus.</summary>
    public bool? Material { get; init; }

    /// <summary>Free-text description of the material component, e.g. "a tiny ball of bat guano and sulfur".</summary>
    public string? MaterialText { get; init; }

    /// <summary>Gold-piece value of a costly material component, if any (e.g. Clone's diamond).</summary>
    public decimal? MaterialCost { get; init; }

    /// <summary>Whether the material component is consumed on cast.</summary>
    public bool? MaterialConsumed { get; init; }

    /// <summary>
    /// dnd5e only (AoN's pf2e spell documents carry no structured damage fields, only prose —
    /// pf2e spell damage validation is out of scope). Damage for a scaling leveled spell, keyed by spell-slot level
    /// (e.g. Fireball: {3: "8d6", 4: "9d6", ...}). dnd5eapi.co keys every leveled damage spell this
    /// way, even non-scaling ones like Magic Missile — there is no separate flat-dice shape to model.
    /// </summary>
    public Dictionary<int, string>? DamageAtSlotLevel { get; init; }

    /// <summary>dnd5e only. Damage for a scaling cantrip, keyed by character level (e.g. Fire Bolt: {1: "1d10", 5: "2d10", 11: "3d10", 17: "4d10"}).</summary>
    public Dictionary<int, string>? DamageAtCharacterLevel { get; init; }

    /// <summary>dnd5e only. Damage type index (e.g. "fire"), paired with DamageAtSlotLevel/DamageAtCharacterLevel.</summary>
    public string? DamageType { get; init; }

    /// <summary>dnd5e only. Ability abbreviation the target saves with (e.g. "dex").</summary>
    public string? SaveType { get; init; }

    /// <summary>dnd5e only. What a successful save does (e.g. "half", "none").</summary>
    public string? SaveSuccess { get; init; }

    /// <summary>dnd5e only. Healing for a scaling spell, keyed by spell-slot level (e.g. Cure Wounds: {1: "1d8 + MOD", 2: "2d8 + MOD", ...}).</summary>
    public Dictionary<int, string>? HealAtSlotLevel { get; init; }

    /// <summary>dnd5e only. Area-of-effect shape (e.g. "sphere", "cone").</summary>
    public string? AreaOfEffectType { get; init; }

    /// <summary>dnd5e only. Area-of-effect size in feet.</summary>
    public int? AreaOfEffectSize { get; init; }

    /// <summary>dnd5e only. Number of independent damage instances per cast (e.g. Magic Missile's darts,
    /// Scorching Ray's rays), keyed by spell-slot level for leveled spells or character level for
    /// scaling cantrips. Omitted (null) means 1 — the existing single-instance behavior.</summary>
    public Dictionary<int, int>? InstanceCountAtSlotLevel { get; init; }
    public Dictionary<int, int>? InstanceCountAtCharacterLevel { get; init; }

    /// <summary>dnd5e only. Per-instance damage dice, when InstanceCount* is set — this is what
    /// DamageAtSlotLevel/DamageAtCharacterLevel means for a multi-instance spell (the API's own field
    /// there is the pre-summed all-instances total and is kept as-is for narrative/reference only).</summary>
    public Dictionary<int, string>? PerInstanceDamageAtSlotLevel { get; init; }
    public Dictionary<int, string>? PerInstanceDamageAtCharacterLevel { get; init; }

    /// <summary>dnd5e only. False for auto-hit spells (Magic Missile) — skips the attack roll entirely.
    /// Omitted means true (the existing behavior).</summary>
    public bool? RequiresAttackRoll { get; init; }

    /// <summary>dnd5e only. True when the damage table is an HP-affect pool, not HP damage
    /// (Sleep's "roll 5d8; the total is how many hit points of creatures this spell can affect").
    /// The resolver refuses to resolve such spells through the damage paths.</summary>
    public bool? DamageIsPool { get; init; }

    /// <summary>dnd5e only. What happens to the immediate damage instance on a missed attack roll.
    /// Omitted/None means the existing behavior (0 damage on miss).</summary>
    public MissBehavior? OnMiss { get; init; }

    /// <summary>dnd5e only. A second damage application scheduled for a later turn boundary
    /// (Acid Arrow's "2d4 at the end of its next turn"). Null means no delayed tick.</summary>
    public DelayedDamageTick? DelayedTick { get; init; }

    /// <summary>dnd5e only. Multi-pool damage (Ice Storm's bludgeoning + cold, Meteor Swarm's
    /// fire + bludgeoning, Flame Strike's fire + radiant), keyed by damage type then spell-slot
    /// level. API-derived when dnd5eapi.co carries every pool cleanly, overlay-supplied when it
    /// doesn't (Flame Strike's upcast entries are unparseable upstream). When present, the
    /// resolver derives each pool from here and ignores DamageAtSlotLevel (kept for
    /// narrative/reference only) — same derived-not-caller-sent rule as multi-instance damage,
    /// because one damageDice string can't express two typed pools.</summary>
    public Dictionary<string, Dictionary<int, string>>? DamagePools { get; init; }

    /// <summary>dnd5e only. Caster's-choice upcast bonus for a multi-pool spell (Flame Strike).
    /// Null means every pool's dice come straight from <see cref="DamagePools"/>.</summary>
    public UpcastPoolChoice? UpcastChoice { get; init; }

    public static SpellDefinition Merge(SpellDefinition child, SpellDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Level = child.Level ?? parent.Level,
            Concentration = child.Concentration ?? parent.Concentration,
            CastingTime = child.CastingTime ?? parent.CastingTime,
            Classes = child.Classes.Count > 0 ? child.Classes : parent.Classes,
            Verbal = child.Verbal ?? parent.Verbal,
            Somatic = child.Somatic ?? parent.Somatic,
            Material = child.Material ?? parent.Material,
            MaterialText = child.MaterialText ?? parent.MaterialText,
            MaterialCost = child.MaterialCost ?? parent.MaterialCost,
            MaterialConsumed = child.MaterialConsumed ?? parent.MaterialConsumed,
            DamageAtSlotLevel = child.DamageAtSlotLevel ?? parent.DamageAtSlotLevel,
            DamageAtCharacterLevel = child.DamageAtCharacterLevel ?? parent.DamageAtCharacterLevel,
            DamageType = child.DamageType ?? parent.DamageType,
            SaveType = child.SaveType ?? parent.SaveType,
            SaveSuccess = child.SaveSuccess ?? parent.SaveSuccess,
            HealAtSlotLevel = child.HealAtSlotLevel ?? parent.HealAtSlotLevel,
            AreaOfEffectType = child.AreaOfEffectType ?? parent.AreaOfEffectType,
            AreaOfEffectSize = child.AreaOfEffectSize ?? parent.AreaOfEffectSize,
            InstanceCountAtSlotLevel = child.InstanceCountAtSlotLevel ?? parent.InstanceCountAtSlotLevel,
            InstanceCountAtCharacterLevel = child.InstanceCountAtCharacterLevel ?? parent.InstanceCountAtCharacterLevel,
            PerInstanceDamageAtSlotLevel = child.PerInstanceDamageAtSlotLevel ?? parent.PerInstanceDamageAtSlotLevel,
            PerInstanceDamageAtCharacterLevel = child.PerInstanceDamageAtCharacterLevel ?? parent.PerInstanceDamageAtCharacterLevel,
            RequiresAttackRoll = child.RequiresAttackRoll ?? parent.RequiresAttackRoll,
            DamageIsPool = child.DamageIsPool ?? parent.DamageIsPool,
            DamagePools = child.DamagePools ?? parent.DamagePools,
            UpcastChoice = child.UpcastChoice ?? parent.UpcastChoice,
            OnMiss = child.OnMiss ?? parent.OnMiss,
            DelayedTick = child.DelayedTick ?? parent.DelayedTick,
        };
}