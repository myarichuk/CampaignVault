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

    /// <summary>5e half-feat: one ability it raises, of the player's choice when it names several (see <see cref="FeatAbilityIncrease"/>).</summary>
    public FeatAbilityIncrease? AbilityIncrease { get; init; }

    /// <summary>5e: armor, weapon and tool proficiencies it gives, joined to the sheet's.</summary>
    public ProficiencyGrants? Proficiencies { get; init; }

    /// <summary>5e: saving throws it makes the character proficient in.</summary>
    public List<string> SavingThrows { get; init; } = [];

    /// <summary>5e: proficiency in the saving throw of the ability <see cref="AbilityIncrease"/> raised.</summary>
    public bool SavingThrowOfIncrease { get; init; }

    /// <summary>5e: how many skills of the player's choice it makes the character proficient in.</summary>
    public int SkillChoices { get; init; }

    /// <summary>5e: extra hit points per character level, from the level it is taken at on.</summary>
    public int HpPerLevel { get; init; }

    /// <summary>5e: spells it gives outright (spell ids), added to the character's cantrips or known spells.</summary>
    public List<string> Spells { get; init; } = [];

    /// <summary>5e: spells it lets the player choose (see <see cref="FeatSpellChoice"/>), added like <see cref="Spells"/>.</summary>
    public List<FeatSpellChoice> SpellChoices { get; init; } = [];

    /// <summary>Whether it gives anything besides roll effects: an ability, proficiencies, saves, skills, hit points, spells.</summary>
    public bool HasGrants =>
        AbilityIncrease is not null || Proficiencies is not null || SavingThrows.Count > 0 || SavingThrowOfIncrease
        || SkillChoices > 0 || HpPerLevel != 0 || Spells.Count > 0 || SpellChoices.Count > 0;

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
            AbilityIncrease = child.AbilityIncrease ?? parent.AbilityIncrease,
            Proficiencies = child.Proficiencies ?? parent.Proficiencies,
            SavingThrows = child.SavingThrows.Count > 0 ? child.SavingThrows : parent.SavingThrows,
            SavingThrowOfIncrease = child.SavingThrowOfIncrease || parent.SavingThrowOfIncrease,
            SkillChoices = child.SkillChoices > 0 ? child.SkillChoices : parent.SkillChoices,
            HpPerLevel = child.HpPerLevel != 0 ? child.HpPerLevel : parent.HpPerLevel,
            Spells = child.Spells.Count > 0 ? child.Spells : parent.Spells,
            SpellChoices = child.SpellChoices.Count > 0 ? child.SpellChoices : parent.SpellChoices,
        };
}

/// <summary>
/// The ability a 5e half-feat raises: <c>{ choose: [Strength, Dexterity], amount: 1 }</c>. One entry is fixed; several (or
/// none, meaning any of the six) are the player's pick when the feat is taken.
/// </summary>
public sealed record FeatAbilityIncrease
{
    public List<string> Choose { get; init; } = [];
    public int Amount { get; init; } = 1;
}

/// <summary>
/// Spells a 5e feat lets the player choose: <c>{ level: 0, count: 2, lists: [bard, cleric, wizard] }</c> is two cantrips from
/// any of those classes' lists.
/// </summary>
public sealed record FeatSpellChoice
{
    public int Level { get; init; }
    public int Count { get; init; } = 1;
    public List<string> Lists { get; init; } = [];
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
