using CampaignVault.Rulesets.Creation;

namespace CampaignVault.Data.Templates;

/// <summary>
/// The character builder's steps for one creation kind (<c>RulesetData/&lt;system&gt;/creation/pc.yaml</c>, named
/// <c>pc</c> or <c>companion</c>). A plugin adds a step with <c>patches: pc</c> and <c>steps+:</c> (placed by the
/// step's <c>after:</c>), or removes one with <c>steps-:</c> (by key).
/// </summary>
public record CreationRecipe : RulesetTemplate
{
    public string? System { get; init; }
    public List<CreationStep> Steps { get; init; } = [];

    /// <summary>
    /// The highest level the builder builds at (5e: 20, with a level choices step). Absent means no limit; the client
    /// keeps its own table for the party step, which asks for a level before any recipe is read.
    /// </summary>
    public int? MaxLevel { get; init; }

    public static CreationRecipe Merge(CreationRecipe child, CreationRecipe parent) =>
        child with
        {
            System = child.System ?? parent.System,
            MaxLevel = child.MaxLevel ?? parent.MaxLevel,
            Description = child.Description ?? parent.Description,
            Steps = child.Steps.Count > 0 ? child.Steps : parent.Steps,
        };
}

/// <summary>
/// A stat block the builder edits field by field (<c>RulesetData/&lt;system&gt;/statblocks/companion.yaml</c>). The
/// client draws a generic editor from it.
/// </summary>
public record StatBlockSchema : RulesetTemplate
{
    public string? System { get; init; }

    /// <summary>The editor's caption ("Stat block" when absent; Narrative's character fields are "Nature").</summary>
    public string? Title { get; init; }

    public List<StatBlockField> Fields { get; init; } = [];

    public static StatBlockSchema Merge(StatBlockSchema child, StatBlockSchema parent) =>
        child with
        {
            System = child.System ?? parent.System,
            Title = child.Title ?? parent.Title,
            Description = child.Description ?? parent.Description,
            Fields = child.Fields.Count > 0 ? child.Fields : parent.Fields,
        };
}

/// <summary>
/// One stat block field, keyed by the system-stats field it writes (JSON name). <see cref="Type"/> is <c>int</c>,
/// <c>text</c>, <c>modifiers</c> (an object of name → whole number, e.g. skill → bonus, its names from <see cref="Source"/>),
/// <c>rows</c> (a list of objects, one per row, their fields from <see cref="Columns"/>: a companion's attacks), or
/// <c>choice</c> (one name from <see cref="Source"/>: a creature type), or <c>list</c> (short entries, a list or one text
/// separated by commas, semicolons or lines; <see cref="Min"/>..<see cref="Max"/> is how many: a Narrative character's
/// descriptors).
/// </summary>
public record StatBlockField
{
    public static readonly string[] Types = ["int", "text", "modifiers", "rows", "choice", "list"];

    public string Key { get; init; } = null!;
    public string? Label { get; init; }
    public string Type { get; init; } = "text";
    /// <summary>The lowest number; for <c>list</c>, the fewest entries (when any are given).</summary>
    public int? Min { get; init; }

    /// <summary>The highest number; for <c>rows</c>, the most rows; for <c>list</c>, the most entries.</summary>
    public int? Max { get; init; }

    /// <summary>What to write, shown in the empty field ("three words, comma-separated"). Defaults to the type or range.</summary>
    public string? Hint { get; init; }

    /// <summary>The draft can't commit without it (HP: a companion with none would be dead on arrival).</summary>
    public bool Required { get; init; }

    /// <summary>Fields with the same group are drawn together (e.g. defense).</summary>
    public string? Group { get; init; }

    /// <summary>Drawn as a narrow box beside its neighbours, like a number (a challenge rating). Numbers always are.</summary>
    public bool Compact { get; init; }

    /// <summary><c>modifiers</c> and <c>choice</c>: the source its names come from (<c>skills</c>, <c>abilities</c>, <c>creatureTypes</c>).</summary>
    public string? Source { get; init; }

    /// <summary><c>modifiers</c> and <c>choice</c>, filled for the client from <see cref="Source"/>: the names it may use. Not read from YAML.</summary>
    public IReadOnlyList<string>? Keys { get; init; }

    /// <summary><c>rows</c> only: what each row holds, in order. The first is its name.</summary>
    public List<StatBlockColumn>? Columns { get; init; }

    /// <summary><c>rows</c> only: what one row is called ("attack"), for the add button.</summary>
    public string? Item { get; init; }
}

/// <summary>
/// One column of a <c>rows</c> stat block field. <see cref="Type"/> is <c>text</c>, <c>int</c> (a whole number within
/// <see cref="Min"/>..<see cref="Max"/>), or <c>dice</c> (damage: dice or a number, then any words, like "1d6+2 piercing").
/// </summary>
public record StatBlockColumn
{
    public static readonly string[] Types = ["text", "int", "dice"];

    public string Key { get; init; } = null!;
    public string? Label { get; init; }
    public string Type { get; init; } = "text";
    public int? Min { get; init; }
    public int? Max { get; init; }
    public bool Required { get; init; }
}
