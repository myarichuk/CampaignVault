using System.Collections;
using System.Reflection;
using CampaignVault.Data.Templates;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// The data-driven <see cref="ICharacterCreation"/>: runs a system's recipe (<c>creation/&lt;kind&gt;.yaml</c>). A system
/// without one gets a recipe with only the identity step, so the builder works for every system (Narrative included).
/// </summary>
public sealed class RecipeCharacterCreation(
    string system,
    CreationRecipeProvider recipes,
    CreationSources sources,
    IReadOnlyDictionary<string, IRecipeValidator> validators) : ICharacterCreation
{
    public const string PcKind = "pc";
    public const string CompanionKind = "companion";

    public string System => system;

    /// <summary>The recipe's steps in order, before <c>when:</c> filtering. Unknown kinds are an argument error.</summary>
    public IReadOnlyList<CreationStep> AllSteps(string kind)
    {
        if (recipes.TryGetRecipe(system, kind, out var recipe) && recipe is not null)
            return Order(recipe.Steps);

        return kind.ToLowerInvariant() switch
        {
            PcKind => [new CreationStep { Key = "identity", Kind = CreationStepKinds.Identity }],
            CompanionKind =>
            [
                new CreationStep
                {
                    Key = "identity",
                    Kind = CreationStepKinds.Identity,
                    Schema = recipes.GetStatBlocksForSystem(system).ContainsKey(CompanionKind) ? CompanionKind : null,
                },
            ],
            _ => throw new ArgumentException($"Unknown creation kind '{kind}' for {system}. Use '{PcKind}' or '{CompanionKind}'."),
        };
    }

    /// <summary>The context for one call on <paramref name="draft"/>: path references, options and counts resolve against its choices.</summary>
    public CreationContext ContextFor(string kind, CharacterDraft draft)
    {
        var steps = AllSteps(kind);
        CreationContext? ctx = null;
        ctx = new CreationContext
        {
            System = system,
            Kind = kind,
            Level = Math.Max(1, draft.Level),
            Resolve = path => ResolvePath(steps, draft, path),
            Options = step => OptionsFor(step, draft, ctx!),
            Count = (step, group) => CountFor(steps, step, group, draft, ctx!),
        };
        return ctx;
    }

    public IReadOnlyList<CreationStep> Steps(string kind, CreationContext ctx) =>
        [.. AllSteps(kind).Where(step => IsShown(step, ctx))];

    public IReadOnlyList<CreationOption> Options(CreationStep step, CharacterDraft draft, CreationContext ctx) =>
        ctx.Options(step);

    public IReadOnlyList<CreationIssue> Validate(CharacterDraft draft, CreationContext ctx)
    {
        var issues = new List<CreationIssue>();
        foreach (var step in Steps(ctx.Kind, ctx))
        {
            if (step.Kind == CreationStepKinds.Identity)
            {
                if (string.IsNullOrWhiteSpace(draft.Name))
                    issues.Add(CreationIssue.Error(step.Key, "Give the character a name."));
            }
            else if (!draft.Has(step.Key))
            {
                if (!step.Optional)
                    issues.Add(CreationIssue.Error(step.Key, $"Choose {Title(step)}."));
                continue;
            }

            foreach (var name in ValidatorsFor(step))
            {
                // Recipes are checked at startup, so a missing name here is a step a plugin ICharacterCreation made up.
                if (!validators.TryGetValue(name, out var validator))
                {
                    issues.Add(CreationIssue.Error(step.Key, $"No validator named '{name}' is loaded."));
                    continue;
                }

                issues.AddRange(validator.Validate(draft, step, ctx));
            }
        }

        return issues;
    }

    /// <summary>The validators a step runs: its kind's own, then the ones the recipe lists.</summary>
    public static IEnumerable<string> ValidatorsFor(CreationStep step) =>
        KindValidators(step.Kind).Concat(step.Validators).Distinct(StringComparer.Ordinal);

    public static IReadOnlyList<string> KindValidators(string kind) => kind switch
    {
        CreationStepKinds.PickOne => [RecipeValidatorNames.PickOneOption],
        CreationStepKinds.PickN or CreationStepKinds.Feats => [RecipeValidatorNames.PickNOption, RecipeValidatorNames.PickNCount],
        CreationStepKinds.Allocate => [RecipeValidatorNames.PickNOption, RecipeValidatorNames.PickNCount],
        CreationStepKinds.AbilityScores =>
        [
            RecipeValidatorNames.AbilityScoresMethod,
            RecipeValidatorNames.AbilityScoresStandardArray,
            RecipeValidatorNames.AbilityScoresPointBuy,
            RecipeValidatorNames.AbilityScoresRollRange,
        ],
        CreationStepKinds.Spells => [RecipeValidatorNames.SpellsCountForLevel],
        _ => [],
    };

    /// <summary>
    /// The base score the draft chose for an ability plus the chosen race's bonus, or null before abilities are chosen.
    /// Racial bonuses come from the race template only, never from the client.
    /// </summary>
    public int? AbilityScore(string kind, CharacterDraft draft, string ability)
    {
        var steps = AllSteps(kind);
        var abilityStep = steps.FirstOrDefault(s => s.Kind == CreationStepKinds.AbilityScores);
        var choice = abilityStep is null ? null : draft.Get<AbilityScoreChoice>(abilityStep.Key);
        var score = choice?.Scores.FirstOrDefault(kv => kv.Key.Equals(ability, StringComparison.OrdinalIgnoreCase));
        if (score is not { Key: not null } found)
            return null;

        var race = ChosenTemplate(steps, draft, CreationSources.Races) as RaceDefinition;
        var bonus = race?.AbilityBonuses.FirstOrDefault(kv => kv.Key.Equals(ability, StringComparison.OrdinalIgnoreCase)).Value ?? 0;
        return found.Value + bonus;
    }

    private bool IsShown(CreationStep step, CreationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(step.When) && !WhenHolds(step.When, ctx))
            return false;

        // A spells step for a class with nothing to pick at this level (a level-1 paladin) has nothing to ask.
        if (step.Kind == CreationStepKinds.Spells && ctx.Resolve("class") is not null)
        {
            return (ctx.Count(step, SpellGroups.Cantrips) ?? 0) > 0
                   || (ctx.Count(step, SpellGroups.Known) ?? 0) > 0
                   || (ctx.Count(step, SpellGroups.Prepared) ?? 0) > 0;
        }

        return true;
    }

    /// <summary><c>path</c>, <c>path == value</c> or <c>path != value</c>; text comparison ignoring case. An unresolved path is empty.</summary>
    internal static bool WhenHolds(string condition, CreationContext ctx)
    {
        foreach (var (op, negate) in new[] { ("!=", true), ("==", false) })
        {
            var at = condition.IndexOf(op, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var left = Text(ctx.Resolve(condition[..at].Trim()));
            var right = condition[(at + op.Length)..].Trim().Trim('"', '\'');
            var equal = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            return negate ? !equal : equal;
        }

        var value = ctx.Resolve(condition.Trim());
        return value switch
        {
            null => false,
            bool b => b,
            int i => i != 0,
            string s => !string.IsNullOrWhiteSpace(s) && !s.Equals("None", StringComparison.OrdinalIgnoreCase),
            IList list => list.Count > 0,
            Enum e => !e.ToString().Equals("None", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        IList list => string.Join(",", list.Cast<object?>()),
        _ => value.ToString() ?? string.Empty,
    };

    private IReadOnlyList<CreationOption> OptionsFor(CreationStep step, CharacterDraft draft, CreationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(step.Source))
            return [];

        var steps = AllSteps(ctx.Kind);
        var classTemplate = ChosenTemplate(steps, draft, CreationSources.Classes) as ClassDefinition;
        var options = sources.Options(system, step.Source, classTemplate, MaxSpellLevel(classTemplate, ctx.Level));

        if (!string.IsNullOrWhiteSpace(step.Exclude) && ctx.Resolve(step.Exclude) is IEnumerable excluded and not string)
        {
            var drop = excluded.Cast<object?>().Select(o => o?.ToString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            options = [.. options.Where(o => !drop.Contains(o.Id))];
        }

        return options;
    }

    private int? CountFor(IReadOnlyList<CreationStep> steps, CreationStep step, string? group, CharacterDraft draft, CreationContext ctx)
    {
        if (step.Kind == CreationStepKinds.Spells && group is not null)
            return SpellCount(steps, group, draft, ctx);

        if (!string.IsNullOrWhiteSpace(step.CountFrom))
        {
            var value = ctx.Resolve(step.CountFrom);
            if (value is int n)
                return n;
            if (value is not null && int.TryParse(value.ToString(), out var parsed))
                return parsed;
        }

        return step.Count;
    }

    private int? SpellCount(IReadOnlyList<CreationStep> steps, string group, CharacterDraft draft, CreationContext ctx)
    {
        if (ChosenTemplate(steps, draft, CreationSources.Classes) is not ClassDefinition cls
            || !sources.ProgressionProvider.TryGetProgression(system, cls.Name, out var progression))
            return null;

        return group switch
        {
            SpellGroups.Cantrips => progression.CountAtLevel(ctx.Level, l => l.CantripsKnown),
            SpellGroups.Known => progression.CountAtLevel(ctx.Level, l => l.SpellsKnown),
            SpellGroups.Prepared when progression.PreparedSpells is { } prepared =>
                prepared.CountFor(ctx.Level, Modifier(AbilityScore(ctx.Kind, draft, prepared.Ability) ?? 10)),
            _ => 0,
        };
    }

    private static int Modifier(int score) => (int)Math.Floor((score - 10) / 2.0);

    /// <summary>The highest spell level a class can cast at a character level, by caster type (SRD slot tables).</summary>
    internal static int MaxSpellLevel(ClassDefinition? cls, int level) => cls?.CasterType switch
    {
        CasterType.Full => Math.Min(9, (level + 1) / 2),
        CasterType.Warlock => Math.Min(5, (level + 1) / 2),
        CasterType.Half => level < 2 ? 0 : Math.Min(5, (level + 3) / 4),
        CasterType.HalfRoundUp => Math.Min(5, (level + 3) / 4),
        CasterType.Third => level < 3 ? 0 : Math.Min(4, (level - 1) / 6 + 1),
        _ => 0,
    };

    private RulesetTemplate? ChosenTemplate(IReadOnlyList<CreationStep> steps, CharacterDraft draft, string source)
    {
        var step = steps.FirstOrDefault(s => s.Kind == CreationStepKinds.PickOne && string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
        var id = step is null ? null : draft.GetString(step.Key);
        return id is null ? null : sources.Template(system, source, id);
    }

    /// <summary>
    /// <c>draft.&lt;field&gt;</c>, or <c>&lt;stepKey&gt;[.field...]</c>: a pickOne with a template source resolves to the
    /// chosen template, any other step to its raw choice (string or list).
    /// </summary>
    private object? ResolvePath(IReadOnlyList<CreationStep> steps, CharacterDraft draft, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return null;

        object? current;
        if (segments[0].Equals("draft", StringComparison.OrdinalIgnoreCase))
        {
            current = draft;
        }
        else
        {
            var step = steps.FirstOrDefault(s => s.Key.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
            if (step is null || !draft.Has(step.Key))
                return null;

            var id = draft.GetString(step.Key);
            current = step.Kind == CreationStepKinds.PickOne && id is not null
                ? (object?)sources.Template(system, step.Source, id) ?? id
                : draft.GetList(step.Key);
        }

        foreach (var segment in segments.Skip(1))
        {
            current = Member(current, segment);
            if (current is null)
                return null;
        }

        return current;
    }

    private static object? Member(object? target, string name)
    {
        switch (target)
        {
            case null:
                return null;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (string.Equals(entry.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
                        return entry.Value;
                }

                return null;
            default:
                return target.GetType()
                    .GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?.GetValue(target);
        }
    }

    /// <summary>File order, except a step with <c>after:</c> moves right behind that key (its first match; unknown keys stay put).</summary>
    private static IReadOnlyList<CreationStep> Order(IReadOnlyList<CreationStep> steps)
    {
        var ordered = steps.Where(s => string.IsNullOrWhiteSpace(s.After)).ToList();
        foreach (var step in steps.Where(s => !string.IsNullOrWhiteSpace(s.After)))
        {
            var anchor = ordered.FindLastIndex(s => s.Key.Equals(step.After, StringComparison.OrdinalIgnoreCase));
            if (anchor < 0)
                ordered.Add(step);
            else
                ordered.Insert(anchor + 1, step);
        }

        return ordered;
    }

    private static string Title(CreationStep step) => step.Prompt ?? CreationSources.Label(step.Key).ToLowerInvariant();
}
