namespace CampaignVault.Data.Templates;

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
        };
}