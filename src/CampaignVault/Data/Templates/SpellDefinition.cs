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
        };
}