using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// The fixed set of builder step kinds. The client draws one widget per kind, so a new system (or a plugin system)
/// needs no client work. A recipe naming any other kind is a startup error.
/// </summary>
public static class CreationStepKinds
{
    /// <summary>One option from <see cref="CreationStep.Source"/>; the choice is a string.</summary>
    public const string PickOne = "pickOne";

    /// <summary>Several options from <see cref="CreationStep.Source"/>; the choice is a string list.</summary>
    public const string PickN = "pickN";

    /// <summary>The six scores by standard array, point buy or roll; the choice is <see cref="AbilityScoreChoice"/>.</summary>
    public const string AbilityScores = "abilityScores";

    /// <summary>Free boosts spread over abilities (PF2e); the choice is a string list of ability names.</summary>
    public const string Allocate = "allocate";

    /// <summary>Cantrips and leveled spells; the choice is <see cref="SpellChoice"/>.</summary>
    public const string Spells = "spells";

    /// <summary>Feats from <see cref="CreationStep.Source"/>; the choice is a string list.</summary>
    public const string Feats = "feats";

    /// <summary>Name, concept and look live on the draft itself; a <see cref="CreationStep.Schema"/> adds a stat block.</summary>
    public const string Identity = "identity";

    /// <summary>The progression's pending choices for levels above 1; the choice is an object keyed by choice key.</summary>
    public const string LevelChoices = "levelChoices";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PickOne, PickN, AbilityScores, Allocate, Spells, Feats, Identity, LevelChoices,
    };
}

/// <summary>
/// One step of a creation recipe (<c>RulesetData/&lt;system&gt;/creation/&lt;kind&gt;.yaml</c>). Constraints are data:
/// counts, path references (<c>class.skillChoices.count</c>), a simple <see cref="When"/> condition and named
/// validators. Anything smarter is a named <see cref="IRecipeValidator"/> in C#, never an expression in YAML.
/// </summary>
public sealed record CreationStep
{
    /// <summary>The key the draft's choice is stored under (<c>race</c>, <c>skills</c>, ...). Unique within a recipe.</summary>
    public string Key { get; init; } = null!;

    /// <summary>One of <see cref="CreationStepKinds"/>.</summary>
    public string Kind { get; init; } = null!;

    /// <summary>What the client shows as the step's title. Falls back to the key.</summary>
    public string? Prompt { get; init; }

    /// <summary>Where options come from: races, classes, backgrounds, classSkills, skills, spells, feats, creatures, startingEquipment.</summary>
    public string? Source { get; init; }

    /// <summary>A fixed pick count for pickN/feats/allocate.</summary>
    public int? Count { get; init; }

    /// <summary>A path whose value is the pick count (e.g. <c>class.skillChoices.count</c>). Wins over <see cref="Count"/> when it resolves.</summary>
    public string? CountFrom { get; init; }

    /// <summary>A path to a list of option ids this step must not offer (e.g. <c>background.skillProficiencies</c>).</summary>
    public string? Exclude { get; init; }

    /// <summary>
    /// Shows the step only when the condition holds: <c>path</c> (set and not None/0/false), <c>path == value</c> or
    /// <c>path != value</c>. Values compare as text, ignoring case.
    /// </summary>
    public string? When { get; init; }

    /// <summary>The draft may leave it empty.</summary>
    public bool Optional { get; init; }

    /// <summary>Extra named validators (see <see cref="IRecipeValidator"/>). The kind's own validators always run.</summary>
    public List<string> Validators { get; init; } = [];

    /// <summary>abilityScores only: which generation methods are allowed and their rules.</summary>
    public AbilityScoreMethods? Methods { get; init; }

    /// <summary>identity only: the stat block schema to edit (a <c>statblocks/</c> template name).</summary>
    public string? Schema { get; init; }

    /// <summary>The system-stats field the choice is written to (JSON name). Defaults to a field named like the key, else a level-1 choice record.</summary>
    public string? Target { get; init; }

    /// <summary>Places this step right after the step with this key (a plugin adding a step before <c>identity</c>). Otherwise steps keep file order.</summary>
    public string? After { get; init; }
}

/// <summary>Allowed ability-score methods for an <see cref="CreationStepKinds.AbilityScores"/> step. A null method is not allowed.</summary>
public sealed record AbilityScoreMethods
{
    public List<int>? StandardArray { get; init; }
    public PointBuyRules? PointBuy { get; init; }

    /// <summary>A dice expression the client rolls per score (<c>4d6dropLowest</c>); the server checks the result's range.</summary>
    public string? Roll { get; init; }
}

public sealed record PointBuyRules
{
    public int Budget { get; init; }
    public int Min { get; init; }
    public int Max { get; init; }

    /// <summary>Cumulative cost of each score from <see cref="Min"/>.</summary>
    public Dictionary<int, int> Cost { get; init; } = [];
}

/// <summary>The choice of an abilityScores step: the method used and the scores before racial bonuses.</summary>
public sealed record AbilityScoreChoice
{
    /// <summary>standardArray, pointBuy or roll.</summary>
    public string Method { get; init; } = null!;

    /// <summary>Ability name (Strength, ...) → base score. Racial bonuses come from the race step, never from here.</summary>
    public Dictionary<string, int> Scores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The choice of a spells step.</summary>
public sealed record SpellChoice
{
    public List<string> Cantrips { get; init; } = [];

    /// <summary>Spells known, or a wizard's spellbook.</summary>
    public List<string> Known { get; init; } = [];

    /// <summary>Prepared today (prepared casters); empty means the DM picks.</summary>
    public List<string> Prepared { get; init; } = [];
}

/// <summary>
/// The draft the client builds step by step and sends whole on every call. Choices are keyed by recipe step key,
/// so the contract is the same for every system.
/// </summary>
public sealed record CharacterDraft
{
    /// <summary>The character id once committed (or the id to commit to). Committing the same id again updates it.</summary>
    public string? Id { get; init; }

    /// <summary>The recipe: <c>pc</c> or <c>companion</c>.</summary>
    public string Kind { get; init; } = "pc";

    /// <summary>The campaign's system; empty means the campaign's.</summary>
    public string? System { get; init; }

    public int Level { get; init; } = 1;
    public string? Name { get; init; }

    /// <summary>One line on who they are; stored in notes.</summary>
    public string? Concept { get; init; }

    /// <summary>What they look like; stored as the appearance.</summary>
    public string? Look { get; init; }

    /// <summary>Step key → a string, a string list, or an object (<see cref="AbilityScoreChoice"/>, <see cref="SpellChoice"/>).</summary>
    public Dictionary<string, JsonElement> Choices { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The draft with one choice set (serialized to JSON), for code that builds drafts.</summary>
    public CharacterDraft With(string key, object value)
    {
        var choices = new Dictionary<string, JsonElement>(Choices, StringComparer.OrdinalIgnoreCase)
        {
            [key] = JsonSerializer.SerializeToElement(value, ChoiceJson),
        };
        return this with { Choices = choices };
    }

    /// <summary>The choice as one string, or null when absent or not a string.</summary>
    public string? GetString(string key) =>
        Choices.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The choice as a string list (a single string counts as one), empty when absent.</summary>
    public IReadOnlyList<string> GetList(string key)
    {
        if (!Choices.TryGetValue(key, out var v))
            return [];

        return v.ValueKind switch
        {
            JsonValueKind.String => [v.GetString()!],
            JsonValueKind.Array => [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
            _ => [],
        };
    }

    /// <summary>The choice as an object of type <typeparamref name="T"/>, or null when absent or the wrong shape.</summary>
    public T? Get<T>(string key) where T : class
    {
        if (!Choices.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return v.Deserialize<T>(ChoiceJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool Has(string key) =>
        Choices.TryGetValue(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        && !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0)
        && !(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()));

    private static readonly JsonSerializerOptions ChoiceJson = new(JsonSerializerDefaults.Web);
}

/// <summary>An option a step offers. <see cref="Group"/> splits one step's options (spells: <c>cantrips</c> / <c>spells</c>).</summary>
public sealed record CreationOption(string Id, string Label, string? Description = null, string? Group = null);

/// <summary>A problem with a draft: an error blocks commit, a warning doesn't.</summary>
public sealed record CreationIssue(string Step, string Message)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsWarning { get; init; }

    public static CreationIssue Error(string step, string message) => new(step, message);
    public static CreationIssue Warning(string step, string message) => new(step, message) { IsWarning = true };
}

/// <summary>
/// What a creation call knows about the draft it is working on. The host builds one per call; the functions resolve
/// against the draft's current choices.
/// </summary>
public sealed class CreationContext
{
    public required string System { get; init; }
    public required string Kind { get; init; }
    public int Level { get; init; } = 1;

    /// <summary>
    /// A recipe path against the draft: the first segment is a step key (its chosen template, e.g. <c>class</c>) or
    /// <c>draft</c>; the rest walks the template's fields (<c>class.skillChoices.count</c>). Null when it doesn't resolve.
    /// </summary>
    public required Func<string, object?> Resolve { get; init; }

    /// <summary>The options a step offers for this draft (after <see cref="CreationStep.Exclude"/>).</summary>
    public required Func<CreationStep, IReadOnlyList<CreationOption>> Options { get; init; }

    /// <summary>
    /// How many picks a step wants for this draft (<see cref="CreationStep.CountFrom"/>, else <see cref="CreationStep.Count"/>),
    /// or null for no fixed count. With a group, that group's count (spells: <c>cantrips</c>, <c>known</c>, <c>prepared</c>).
    /// </summary>
    public required Func<CreationStep, string?, int?> Count { get; init; }
}

/// <summary>
/// A system's character creation. Optional: implement it on a ruleset plugin (or alongside one) only when the
/// data-driven recipe can't express the system. Without one, the host runs the recipe in the system's
/// <c>creation/</c> folder. Preview and commit always stay in the host (bootstrap pipeline and world_build).
/// </summary>
public interface ICharacterCreation
{
    string System { get; }

    /// <summary>The steps for a creation kind (<c>pc</c>, <c>companion</c>), already filtered by <see cref="CreationStep.When"/>.</summary>
    IReadOnlyList<CreationStep> Steps(string kind, CreationContext ctx);

    IReadOnlyList<CreationOption> Options(CreationStep step, CharacterDraft draft, CreationContext ctx);

    /// <summary>Every problem with the draft; an empty list means it can be committed.</summary>
    IReadOnlyList<CreationIssue> Validate(CharacterDraft draft, CreationContext ctx);
}

/// <summary>
/// A named rule a recipe step can list under <c>validators:</c>. Found by convention scanning (the host and plugin
/// assemblies), like <c>IPluginTraitsUpgrader</c>. A recipe naming a validator nobody provides is a startup error.
/// </summary>
public interface IRecipeValidator
{
    /// <summary>The name recipes use (<c>abilityScores.pointBuy</c>). Unique across loaded assemblies.</summary>
    string Name { get; }

    IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx);
}
