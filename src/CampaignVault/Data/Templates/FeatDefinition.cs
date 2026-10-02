using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

public record FeatDefinition : RulesetTemplate
{
    public string System { get; init; } = null!;
    public string? Prerequisite { get; init; }
    public string? MechanicalSummary { get; init; }
    public List<string> ExtraPools { get; init; } = [];

    /// <summary>Classes that can take this feat, e.g. ["fighter"]. Empty for ancestry/general/skill feats.</summary>
    public List<string> Classes { get; init; } = [];

    /// <summary>PF2e: ancestry, class, skill, general or archetype (a skill feat is also a general feat).</summary>
    public string? Category { get; init; }

    /// <summary>PF2e ancestry feats: the ancestries that can take it (ancestry template names).</summary>
    public List<string> Ancestries { get; init; } = [];

    /// <summary>PF2e skill feats: the skills it is for.</summary>
    public List<string> Skills { get; init; } = [];

    /// <summary>
    /// The parts of <see cref="Prerequisite"/> the character builder checks (a skill rank, an ability, another feat, a class
    /// feature). The rest of the text is shown, not checked. Generated for PF2e; written by hand for 5e.
    /// </summary>
    public List<FeatPrerequisite> Prerequisites { get; init; } = [];

    /// <summary>Minimum character level required to take this feat.</summary>
    public int? Level { get; init; }

    /// <summary>
    /// Freeform prerequisite strings (e.g. "Agility 6+"). Data round-trip only — not enforced at
    /// grant time. No attribute-comparator parser or grant-time validation hook exists yet; the
    /// LLM DM is responsible for honoring these when narrating a character taking the perk.
    /// </summary>
    public List<string> Requirements { get; init; } = [];

    /// <summary>
    /// Passive spell-component requirements this feat waives, e.g. "SomaticHandsFull" for a feat that lets a caster work with full hands.
    /// Checked directly against the character's known feats by the casting-component gate
    /// (RulesetActionHandler) — distinct from a StatusEffect-based per-turn waiver (e.g. Subtle Spell),
    /// which uses the WaivesVerbalComponents/WaivesSomaticComponents/WaivesMaterialComponents
    /// StatModifiers keys instead since it's a resource spend, not a passive trait.
    /// </summary>
    public List<string> CastingWaivers { get; init; } = [];

    /// <summary>
    /// Declarative roll effects the engine applies itself (see <see cref="FeatEffect"/>). Closed vocabulary, fixed magnitudes:
    /// no scripting. Conditions are either engine-checked (weapon, toggle) or DM-asserted flags.
    /// </summary>
    public List<FeatEffect> Effects { get; init; } = [];

    /// <summary>
    /// True when the feat's rules are prose the DM applies by judgment (no machine-readable effects). Marks it as knowingly
    /// unimplemented so the wiring audit does not report it.
    /// </summary>
    public bool Adjudicated { get; init; }

    public static FeatDefinition Merge(FeatDefinition child, FeatDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            Description = child.Description ?? parent.Description,
            Prerequisite = child.Prerequisite ?? parent.Prerequisite,
            Prerequisites = child.Prerequisites.Count > 0 ? child.Prerequisites : parent.Prerequisites,
            MechanicalSummary = child.MechanicalSummary ?? parent.MechanicalSummary,
            ExtraPools = child.ExtraPools.Count > 0 ? child.ExtraPools : parent.ExtraPools,
            Requirements = child.Requirements.Count > 0 ? child.Requirements : parent.Requirements,
            CastingWaivers = child.CastingWaivers.Count > 0 ? child.CastingWaivers : parent.CastingWaivers,
            Classes = child.Classes.Count > 0 ? child.Classes : parent.Classes,
            Category = child.Category ?? parent.Category,
            Ancestries = child.Ancestries.Count > 0 ? child.Ancestries : parent.Ancestries,
            Skills = child.Skills.Count > 0 ? child.Skills : parent.Skills,
            Level = child.Level ?? parent.Level,
            Effects = child.Effects.Count > 0 ? child.Effects : parent.Effects,
            Adjudicated = child.Adjudicated || parent.Adjudicated,
        };
}

/// <summary>
/// One checkable feat prerequisite: set one of <see cref="Skill"/> (with <see cref="Rank"/>), <see cref="Ability"/> (with
/// <see cref="Min"/>), <see cref="Feat"/>, <see cref="ClassFeature"/>, or <see cref="AnyOf"/>.
/// </summary>
public sealed class FeatPrerequisite
{
    /// <summary>A skill, at <see cref="Rank"/> or better ("Athletics", trained).</summary>
    public string? Skill { get; set; }

    /// <summary>trained, expert, master or legendary (PF2e ranks; a 5e skill is trained when proficient).</summary>
    public string? Rank { get; set; }

    /// <summary>An ability at <see cref="Min"/> or more: a 5e score ("Strength", 13) or a PF2e modifier ("Strength", 2).</summary>
    public string? Ability { get; set; }

    public int? Min { get; set; }

    /// <summary>Another feat the character has (its file name: "shield_block").</summary>
    public string? Feat { get; set; }

    /// <summary>A class feature option the character picked (a PF2e progression option id: "leaf", "warrior", "thief").</summary>
    public string? ClassFeature { get; set; }

    /// <summary>Any one of these.</summary>
    public List<FeatPrerequisite> AnyOf { get; set; } = [];
}
