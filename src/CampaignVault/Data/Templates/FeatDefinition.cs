namespace CampaignVault.Data.Templates;

public record FeatDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public string? Prerequisite { get; init; }
    public string? MechanicalSummary { get; init; }
    public List<string> ExtraPools { get; init; } = [];

    /// <summary>Classes that can take this feat, e.g. ["fighter"]. Empty for ancestry/general/skill feats.</summary>
    public List<string> Classes { get; init; } = [];

    /// <summary>Minimum character level required to take this feat.</summary>
    public int? Level { get; init; }

    /// <summary>
    /// Freeform prerequisite strings (e.g. "Agility 6+"). Data round-trip only — not enforced at
    /// grant time. No attribute-comparator parser or grant-time validation hook exists yet; the
    /// LLM DM is responsible for honoring these when narrating a character taking the perk.
    /// </summary>
    public List<string> Requirements { get; init; } = [];

    /// <summary>
    /// Passive spell-component requirements this feat waives, e.g. "SomaticHandsFull" for War Caster.
    /// Checked directly against the character's known feats by the casting-component gate
    /// (RulesetActionHandler) — distinct from a StatusEffect-based per-turn waiver (e.g. Subtle Spell),
    /// which uses the WaivesVerbalComponents/WaivesSomaticComponents/WaivesMaterialComponents
    /// StatModifiers keys instead since it's a resource spend, not a passive trait.
    /// </summary>
    public List<string> CastingWaivers { get; init; } = [];

    public static FeatDefinition Merge(FeatDefinition child, FeatDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Prerequisite = child.Prerequisite ?? parent.Prerequisite,
            MechanicalSummary = child.MechanicalSummary ?? parent.MechanicalSummary,
            ExtraPools = child.ExtraPools.Count > 0 ? child.ExtraPools : parent.ExtraPools,
            Requirements = child.Requirements.Count > 0 ? child.Requirements : parent.Requirements,
            CastingWaivers = child.CastingWaivers.Count > 0 ? child.CastingWaivers : parent.CastingWaivers,
            Classes = child.Classes.Count > 0 ? child.Classes : parent.Classes,
            Level = child.Level ?? parent.Level,
        };
}