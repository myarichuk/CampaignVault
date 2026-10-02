using System.Text.Json;
using System.Text.RegularExpressions;
using CampaignVault.Data.Templates;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>The core validator names. Each step kind runs its own (see <see cref="RecipeCharacterCreation.KindValidators"/>).</summary>
public static class RecipeValidatorNames
{
    public const string PickOneOption = "pickOne.option";
    public const string PickNOption = "pickN.option";
    public const string PickNCount = "pickN.count";
    public const string AbilityScoresMethod = "abilityScores.method";
    public const string AbilityScoresStandardArray = "abilityScores.standardArray";
    public const string AbilityScoresPointBuy = "abilityScores.pointBuy";
    public const string AbilityScoresRollRange = "abilityScores.rollRange";
    public const string SpellsCountForLevel = "spells.countForLevel";
    public const string StatBlockFields = "statBlock.fields";
    public const string CompanionPower = "companion.power";
    public const string FeatPrerequisites = "feat.prerequisites";
}

/// <summary>A pickOne choice must be one of the step's options (when it has a source).</summary>
public sealed class PickOneOptionValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.PickOneOption;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var choice = draft.GetString(step.Key);
        if (choice is null)
        {
            yield return CreationIssue.Error(step.Key, "Pick one option.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(step.Source))
            yield break;

        if (!ctx.Options(step).Any(o => o.Id.Equals(choice, StringComparison.OrdinalIgnoreCase)))
            yield return CreationIssue.Error(step.Key, $"'{choice}' isn't one of the options.");
    }
}

/// <summary>Every pick must be an option, once.</summary>
public sealed class PickNOptionValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.PickNOption;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var picks = draft.GetList(step.Key);
        var duplicates = picks.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            yield return CreationIssue.Error(step.Key, $"Picked more than once: {string.Join(", ", duplicates)}.");

        if (string.IsNullOrWhiteSpace(step.Source))
            yield break;

        var options = ctx.Options(step).Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = picks.Where(p => !options.Contains(p)).ToList();
        if (unknown.Count > 0)
            yield return CreationIssue.Error(step.Key, $"Not among the options: {string.Join(", ", unknown)}.");
    }
}

/// <summary>Exactly the step's count of picks (when it has one).</summary>
public sealed class PickNCountValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.PickNCount;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        if (ctx.Count(step, null) is not { } count)
            yield break;

        var picked = draft.GetList(step.Key).Count;
        if (picked > count)
            yield return CreationIssue.Error(step.Key, $"Too many: pick {count}, not {picked}.");
        else if (picked < count && !(step.Optional && picked == 0))
            yield return CreationIssue.Error(step.Key, $"Pick {count} ({picked} so far).");
    }
}

/// <summary>The method must be one the recipe allows, with a score for each of the six abilities.</summary>
public sealed class AbilityScoresMethodValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.AbilityScoresMethod;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var choice = draft.Get<AbilityScoreChoice>(step.Key);
        if (choice is null)
        {
            yield return CreationIssue.Error(step.Key, "Ability scores must be { method, scores }.");
            yield break;
        }

        var methods = step.Methods;
        var allowed = methods is null
            ? true
            : choice.Method switch
            {
                "standardArray" => methods.StandardArray is not null,
                "pointBuy" => methods.PointBuy is not null,
                "roll" => methods.Roll is not null,
                _ => false,
            };
        if (!allowed)
            yield return CreationIssue.Error(step.Key, $"Method '{choice.Method}' isn't allowed here.");

        var missing = CreationSources.AbilityNames
            .Where(a => !choice.Scores.Keys.Any(k => k.Equals(a, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
            yield return CreationIssue.Error(step.Key, $"Missing scores: {string.Join(", ", missing)}.");

        var extra = choice.Scores.Keys
            .Where(k => !CreationSources.AbilityNames.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (extra.Count > 0)
            yield return CreationIssue.Error(step.Key, $"Not abilities: {string.Join(", ", extra)}.");
    }
}

/// <summary>Standard array: the scores are exactly the array, each value used once.</summary>
public sealed class StandardArrayValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.AbilityScoresStandardArray;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var choice = draft.Get<AbilityScoreChoice>(step.Key);
        if (choice?.Method != "standardArray" || step.Methods?.StandardArray is not { } array)
            yield break;

        var expected = array.Order().ToList();
        var actual = choice.Scores.Values.Order().ToList();
        if (!expected.SequenceEqual(actual))
            yield return CreationIssue.Error(step.Key, $"Standard array: assign {string.Join(", ", array)}, each once.");
    }
}

/// <summary>Point buy: every score within min/max, total cost within the budget, by the recipe's cost table.</summary>
public sealed class PointBuyValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.AbilityScoresPointBuy;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var choice = draft.Get<AbilityScoreChoice>(step.Key);
        if (choice?.Method != "pointBuy" || step.Methods?.PointBuy is not { } rules)
            yield break;

        var spent = 0;
        foreach (var (ability, score) in choice.Scores)
        {
            if (score < rules.Min || score > rules.Max || !rules.Cost.TryGetValue(score, out var cost))
            {
                yield return CreationIssue.Error(step.Key, $"Point buy: {ability} {score} is outside {rules.Min}–{rules.Max}.");
                continue;
            }

            spent += cost;
        }

        if (spent > rules.Budget)
            yield return CreationIssue.Error(step.Key, $"Point buy: {spent} points spent, budget is {rules.Budget}.");
        else if (spent < rules.Budget)
            yield return CreationIssue.Warning(step.Key, $"Point buy: {rules.Budget - spent} of {rules.Budget} points unspent.");
    }
}

/// <summary>Rolled scores: each within what the roll expression can produce (4d6dropLowest → 3–18).</summary>
public sealed partial class RollRangeValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.AbilityScoresRollRange;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var choice = draft.Get<AbilityScoreChoice>(step.Key);
        if (choice?.Method != "roll" || step.Methods?.Roll is not { } roll)
            yield break;

        var (min, max) = Range(roll);
        foreach (var (ability, score) in choice.Scores)
        {
            if (score < min || score > max)
                yield return CreationIssue.Error(step.Key, $"Rolled {ability} {score} can't come from {roll} ({min}–{max}).");
        }
    }

    /// <summary><c>NdS</c> with an optional <c>dropLowest</c>/<c>dropHighest</c> suffix; anything else is 3–18.</summary>
    internal static (int Min, int Max) Range(string roll)
    {
        var match = DiceExpression().Match(roll);
        if (!match.Success)
            return (3, 18);

        var dice = int.Parse(match.Groups[1].Value);
        var sides = int.Parse(match.Groups[2].Value);
        var kept = match.Groups[3].Success ? dice - 1 : dice;
        return (kept, kept * sides);
    }

    [GeneratedRegex(@"^\s*(\d+)d(\d+)\s*(drop(?:Lowest|Highest))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceExpression();
}

/// <summary>
/// Spells: exactly the cantrips and spells known the class has at this level, prepared spells within the daily count
/// (from the spellbook when the class keeps one, else from the class list), every pick on the class's list.
/// </summary>
public sealed class SpellsCountForLevelValidator : IRecipeValidator
{
    public string Name => RecipeValidatorNames.SpellsCountForLevel;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        var choice = draft.Get<SpellChoice>(step.Key);
        if (choice is null)
        {
            yield return CreationIssue.Error(step.Key, "Spells must be { cantrips, known, prepared }.");
            yield break;
        }

        var options = ctx.Options(step);
        var cantripIds = Ids(options, SpellGroups.Cantrips);
        var spellIds = Ids(options, SpellGroups.Known);

        var cantrips = ctx.Count(step, SpellGroups.Cantrips) ?? 0;
        var known = ctx.Count(step, SpellGroups.Known) ?? 0;
        var prepared = ctx.Count(step, SpellGroups.Prepared) ?? 0;

        foreach (var issue in Exactly(step, "cantrips", choice.Cantrips, cantrips, cantripIds))
            yield return issue;
        foreach (var issue in Exactly(step, "spells known", choice.Known, known, spellIds))
            yield return issue;

        if (choice.Prepared.Count > prepared)
        {
            yield return CreationIssue.Error(step.Key, $"Too many prepared: {prepared} a day, not {choice.Prepared.Count}.");
        }
        else if (prepared > 0 && known == 0 && choice.Prepared.Count < prepared)
        {
            // Without a spellbook or known list, preparing is the only spell pick there is.
            yield return CreationIssue.Error(step.Key, $"Prepare {prepared} spells ({choice.Prepared.Count} so far).");
        }

        var pool = known > 0
            ? choice.Known.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : spellIds;
        var notInPool = choice.Prepared.Where(p => !pool.Contains(p)).ToList();
        if (notInPool.Count > 0)
        {
            yield return CreationIssue.Error(step.Key, known > 0
                ? $"Prepared spells must come from the known spells: {string.Join(", ", notInPool)}."
                : $"Not on the class's list at this level: {string.Join(", ", notInPool)}.");
        }
    }

    private static IEnumerable<CreationIssue> Exactly(CreationStep step, string what, List<string> picks, int count, HashSet<string> allowed)
    {
        if (picks.Count != count)
            yield return CreationIssue.Error(step.Key, count == 0 ? $"No {what} at this level." : $"Pick {count} {what} ({picks.Count} picked).");

        var unknown = picks.Where(p => !allowed.Contains(p)).ToList();
        if (unknown.Count > 0)
            yield return CreationIssue.Error(step.Key, $"Not on the class's list at this level: {string.Join(", ", unknown)}.");

        var duplicates = picks.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            yield return CreationIssue.Error(step.Key, $"Picked more than once: {string.Join(", ", duplicates)}.");
    }

    private static HashSet<string> Ids(IReadOnlyList<CreationOption> options, string group) =>
        options.Where(o => o.Group == group).Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A stat block's values against its schema: only known keys, whole numbers inside the field's range, required fields
/// present. The client checks the same ranges, but a model-drafted block never passed through the client's editor.
/// The message starts with the field's label so the step shows which field to fix.
/// </summary>
public sealed class StatBlockFieldsValidator(CreationRecipeProvider recipes) : IRecipeValidator
{
    public string Name => RecipeValidatorNames.StatBlockFields;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(step.Schema)
            || !recipes.GetStatBlocksForSystem(ctx.System).TryGetValue(step.Schema, out var schema))
            yield break;

        var values = draft.Choices.TryGetValue(step.Key, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in values.Keys.Where(k => !schema.Fields.Any(f => f.Key.Equals(k, StringComparison.OrdinalIgnoreCase))))
            yield return CreationIssue.Error(step.Key, $"'{key}' isn't a field of the {schema.Name} stat block.");

        foreach (var field in schema.Fields)
        {
            var label = field.Label ?? field.Key;
            if (!values.TryGetValue(field.Key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (field.Required)
                    yield return CreationIssue.Error(step.Key, $"{label}: required.");
                continue;
            }

            if (field.Type == "int")
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n))
                {
                    yield return CreationIssue.Error(step.Key, $"{label}: must be a whole number.");
                    continue;
                }

                if (field.Min is { } min && n < min)
                    yield return CreationIssue.Error(step.Key, $"{label}: {n} is below {min}.");
                else if (field.Max is { } max && n > max)
                    yield return CreationIssue.Error(step.Key, $"{label}: {n} is above {max}.");
            }
            else if (field.Type == "modifiers")
            {
                foreach (var issue in Modifiers(step.Key, label, field, value, ctx.System))
                    yield return issue;
            }
            else if (field.Type == "choice")
            {
                var names = CreationSources.FieldNames(ctx.System, field.Source);
                var picked = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
                if (picked == null || !names.Contains(picked, StringComparer.OrdinalIgnoreCase))
                    yield return CreationIssue.Error(step.Key, $"{label}: '{(picked ?? value.ToString())}' isn't one of: {string.Join(", ", names)}.");
            }
            else if (field.Type == "rows")
            {
                foreach (var issue in Rows(step.Key, label, field, value))
                    yield return issue;
            }
            else if (field.Type == "list")
            {
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                {
                    yield return CreationIssue.Error(step.Key, $"{label}: must be text, its entries separated by commas.");
                    continue;
                }

                var count = DraftCharacterMapper.ListEntries(value).Count;
                if (count > 0 && field.Min is { } fewest && count < fewest)
                    yield return CreationIssue.Error(step.Key, $"{label}: {Many(fewest, field.Max)}, separated by commas (you gave {count}).");
                else if (field.Max is { } most && count > most)
                    yield return CreationIssue.Error(step.Key, $"{label}: {Many(field.Min, most)}, separated by commas (you gave {count}).");
            }
            // A bare number is fine as text (a challenge rating of 1).
            else if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                yield return CreationIssue.Error(step.Key, $"{label}: must be text.");
            }
        }
    }

    /// <summary>"three", "up to three", "two to four".</summary>
    private static string Many(int? fewest, int? most) => (fewest, most) switch
    {
        ({ } a, { } b) when a == b => Words(a),
        ({ } a, { } b) => $"{Words(a)} to {Words(b)}",
        (null, { } b) => $"up to {Words(b)}",
        ({ } a, null) => $"at least {Words(a)}",
        _ => "any number",
    };

    private static string Words(int n) => n is >= 0 and <= 10
        ? new[] { "none", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" }[n]
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A <c>modifiers</c> field: an object of name → whole number (or the templates' "Perception +5" text), each name
    /// from the field's source, once.
    /// </summary>
    private static IEnumerable<CreationIssue> Modifiers(string stepKey, string label, StatBlockField field, JsonElement value, string system)
    {
        value = StatModifierText.Normalize(value);
        if (value.ValueKind != JsonValueKind.Object)
        {
            yield return CreationIssue.Error(stepKey, $"{label}: must be names with whole numbers, like {{\"Perception\": 4}}.");
            yield break;
        }

        var names = CreationSources.FieldNames(system, field.Source);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.EnumerateObject())
        {
            if (names.Count > 0 && !names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return CreationIssue.Error(stepKey, $"{label}: '{entry.Name}' is not one of the {field.Source}.");
                continue;
            }

            if (!seen.Add(entry.Name))
            {
                yield return CreationIssue.Error(stepKey, $"{label}: {entry.Name} is listed twice.");
                continue;
            }

            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt32(out var n))
                yield return CreationIssue.Error(stepKey, $"{label}: {entry.Name} must be a whole number.");
            else if (field.Min is { } min && n < min)
                yield return CreationIssue.Error(stepKey, $"{label}: {entry.Name} {n:+0;-0;0} is below {min}.");
            else if (field.Max is { } max && n > max)
                yield return CreationIssue.Error(stepKey, $"{label}: {entry.Name} {n:+0;-0;0} is above {max}.");
        }
    }

    /// <summary>
    /// A <c>rows</c> field: a list (or the templates' "Bite +3, 1d6+1 piercing" text) of up to <c>max</c> rows, each with
    /// only the field's columns, required ones filled, numbers whole and in range, damage as dice.
    /// </summary>
    private static IEnumerable<CreationIssue> Rows(string stepKey, string label, StatBlockField field, JsonElement value)
    {
        var columns = field.Columns ?? [];
        value = StatRowsText.Normalize(value, columns);
        if (value.ValueKind != JsonValueKind.Array)
        {
            yield return CreationIssue.Error(stepKey, $"{label}: must be a list of rows, like [{{\"{columns.FirstOrDefault()?.Key ?? "name"}\": \"...\"}}].");
            yield break;
        }

        var count = value.GetArrayLength();
        if (field.Max is { } most && count > most)
            yield return CreationIssue.Error(stepKey, $"{label}: {count} rows, at most {most}.");

        var index = 0;
        foreach (var row in value.EnumerateArray())
        {
            index++;
            if (row.ValueKind != JsonValueKind.Object)
            {
                yield return CreationIssue.Error(stepKey, $"{label}: row {index} must be an object.");
                continue;
            }

            var name = columns.Count > 0 && row.TryGetProperty(columns[0].Key, out var n) && n.ValueKind == JsonValueKind.String
                       && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!.Trim()
                : $"row {index}";
            foreach (var extra in row.EnumerateObject().Where(p => !columns.Any(c => c.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
                yield return CreationIssue.Error(stepKey, $"{label}: {name} has '{extra.Name}', which isn't a column ({string.Join(", ", columns.Select(c => c.Key))}).");

            foreach (var column in columns)
            {
                var what = (column.Label ?? column.Key).ToLowerInvariant();
                var cell = row.EnumerateObject().FirstOrDefault(p => p.Name.Equals(column.Key, StringComparison.OrdinalIgnoreCase)).Value;
                var empty = cell.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                            || (cell.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(cell.GetString()));
                if (empty)
                {
                    if (column.Required)
                        yield return CreationIssue.Error(stepKey, $"{label}: {name} needs its {what}.");
                    continue;
                }

                if (column.Type == "int")
                {
                    if (cell.ValueKind != JsonValueKind.Number || !cell.TryGetInt32(out var v))
                        yield return CreationIssue.Error(stepKey, $"{label}: {name}'s {what} must be a whole number.");
                    else if (column.Min is { } min && v < min)
                        yield return CreationIssue.Error(stepKey, $"{label}: {name}'s {what} {v:+0;-0;0} is below {min}.");
                    else if (column.Max is { } max && v > max)
                        yield return CreationIssue.Error(stepKey, $"{label}: {name}'s {what} {v:+0;-0;0} is above {max}.");
                }
                else if (cell.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                {
                    yield return CreationIssue.Error(stepKey, $"{label}: {name}'s {what} must be text.");
                }
                else if (column.Type == "dice" && !StatRowsText.IsDamage(cell.ValueKind == JsonValueKind.String ? cell.GetString() : cell.ToString()))
                {
                    yield return CreationIssue.Error(stepKey, $"{label}: {name}'s {what} must start with dice or a number, like 1d6+2 piercing.");
                }
            }
        }
    }
}

/// <summary>
/// Warns, never blocks, when a companion's level is more than one away from the party's (set as <c>draft.partyLevel</c>).
/// Too strong steals the fights; too weak dies in the first one.
/// </summary>
public sealed class CompanionPowerValidator : IRecipeValidator
{
    public const int Band = 1;

    public string Name => RecipeValidatorNames.CompanionPower;

    public IEnumerable<CreationIssue> Validate(CharacterDraft draft, CreationStep step, CreationContext ctx)
    {
        if (draft.PartyLevel is not { } party)
            yield break;

        if (draft.Level > party + Band)
            yield return CreationIssue.Warning(step.Key, $"Level {draft.Level} is above the party (level {party}); a companion is meant to stay within {Band} of it.");
        else if (draft.Level < party - Band)
            yield return CreationIssue.Warning(step.Key, $"Level {draft.Level} is well below the party (level {party}); it will not last a fight.");
    }
}
