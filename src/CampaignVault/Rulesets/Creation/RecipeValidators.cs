using System.Text.RegularExpressions;

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
