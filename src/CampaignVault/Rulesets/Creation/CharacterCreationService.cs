using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>The character builder's preview: the derived sheet plus every problem with the draft.</summary>
public sealed record CreationPreview(
    Character Character,
    IReadOnlyList<CreationIssue> Errors,
    IReadOnlyList<CreationIssue> Warnings,
    IReadOnlyList<string> Notes);

/// <summary>
/// The character builder behind the <c>character_builder</c> tool. Steps, options and validation come from the
/// system's <see cref="ICharacterCreation"/> (a plugin's, else <see cref="RecipeCharacterCreation"/>); preview always
/// runs the system's bootstrap pipeline on an in-memory character, and commit (in the tool) goes through world_build.
/// </summary>
public sealed class CharacterCreationService
{
    private readonly IRulesetModuleSelector _rulesets;
    private readonly CreationRecipeProvider _recipes;
    private readonly CreationSources _sources;
    private readonly CharacterBootstrapOrchestrator _bootstrap;
    private readonly IReadOnlyDictionary<string, IRecipeValidator> _validators;

    public CharacterCreationService(
        IRulesetModuleSelector rulesets,
        CreationRecipeProvider recipes,
        CreationSources sources,
        IEnumerable<IRecipeValidator> validators,
        CharacterBootstrapOrchestrator bootstrap)
    {
        _rulesets = rulesets;
        _recipes = recipes;
        _sources = sources;
        _bootstrap = bootstrap;

        var byName = new Dictionary<string, IRecipeValidator>(StringComparer.Ordinal);
        foreach (var validator in validators)
        {
            if (byName.TryGetValue(validator.Name, out var other) && other.GetType() != validator.GetType())
            {
                throw new InvalidOperationException(
                    $"Two recipe validators are named '{validator.Name}': {other.GetType().FullName} and {validator.GetType().FullName}.");
            }

            byName[validator.Name] = validator;
        }

        _validators = byName;
    }

    public RecipeCharacterCreation Recipe(string system) => new(system, _recipes, _sources, _validators);

    /// <summary>The system's creation: a plugin's <see cref="ICharacterCreation"/> when one is registered, else its recipe.</summary>
    public ICharacterCreation For(string system) => _rulesets.GetCreation(system) ?? Recipe(system);

    public Data.Templates.StatBlockSchema? StatBlock(string system, string name) =>
        _recipes.GetStatBlocksForSystem(system).GetValueOrDefault(name);

    public CreationContext Context(string system, CharacterDraft draft) => Recipe(system).ContextFor(draft.Kind, draft);

    public IReadOnlyList<CreationStep> Steps(string system, CharacterDraft draft) =>
        For(system).Steps(draft.Kind, Context(system, draft));

    public IReadOnlyList<CreationOption> Options(string system, string stepKey, CharacterDraft draft)
    {
        var step = Recipe(system).AllSteps(draft.Kind).FirstOrDefault(s => s.Key.Equals(stepKey, StringComparison.OrdinalIgnoreCase))
                   ?? Steps(system, draft).FirstOrDefault(s => s.Key.Equals(stepKey, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No step '{stepKey}' in the {system} '{draft.Kind}' recipe.");
        return For(system).Options(step, draft, Context(system, draft));
    }

    public IReadOnlyList<CreationIssue> Validate(string system, CharacterDraft draft) =>
        For(system).Validate(draft, Context(system, draft));

    /// <summary>The draft as an un-bootstrapped character (what world_build receives on a first commit).</summary>
    public Character ToCharacter(string system, CharacterDraft draft, string id) =>
        DraftCharacterMapper.ToCharacter(draft, system, id, Steps(system, draft), _sources);

    /// <summary>
    /// Builds the character in memory and runs the system's creation bootstrap (HP, proficiency, saves, spellcasting,
    /// racial bonuses), like world_build does for a new character. Nothing is written.
    /// </summary>
    public async Task<CreationPreview> PreviewAsync(string system, CharacterDraft draft, string? id = null, CancellationToken ct = default)
    {
        var issues = Validate(system, draft);
        var character = ToCharacter(system, draft, id ?? draft.Id ?? "chars/preview");
        var notes = new List<string>();

        if (_rulesets.IsRegistered(system))
        {
            var report = await _bootstrap.ApplyCreationAsync(new BootstrapContext
            {
                Character = character,
                ActiveSystem = system,
                Trigger = BootstrapTrigger.Create,
            }, ct);
            notes.AddRange(report.Messages);
        }
        else
        {
            notes.Add($"No ruleset module is loaded for '{system}', so nothing was derived.");
        }

        return new CreationPreview(
            character,
            [.. issues.Where(i => !i.IsWarning)],
            [.. issues.Where(i => i.IsWarning)],
            notes);
    }

    /// <summary>
    /// Checks every recipe on disk: known step kinds and sources, unique keys, existing stat block schemas, and validator
    /// names somebody provides. Throws one error listing every problem, so a bad recipe stops the server at startup.
    /// </summary>
    public void ValidateRecipes()
    {
        var problems = new List<string>();
        foreach (var system in _recipes.Systems)
        {
            var schemas = _recipes.GetStatBlocksForSystem(system);
            foreach (var (kind, recipe) in _recipes.GetRecipesForSystem(system))
            {
                var where = $"{system} recipe '{kind}'";
                foreach (var duplicate in recipe.Steps.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    problems.Add($"{where}: step key '{duplicate.Key}' is used {duplicate.Count()} times.");

                foreach (var step in recipe.Steps)
                {
                    var at = $"{where}, step '{step.Key}'";
                    if (string.IsNullOrWhiteSpace(step.Key))
                        problems.Add($"{where}: a step has no key.");
                    if (!CreationStepKinds.All.Contains(step.Kind ?? string.Empty))
                        problems.Add($"{at}: unknown kind '{step.Kind}' (use {string.Join(", ", CreationStepKinds.All)}).");
                    if (!string.IsNullOrWhiteSpace(step.Source) && !CreationSources.All.Contains(step.Source))
                        problems.Add($"{at}: unknown source '{step.Source}' (use {string.Join(", ", CreationSources.All)}).");
                    if (!string.IsNullOrWhiteSpace(step.Schema) && !schemas.ContainsKey(step.Schema))
                        problems.Add($"{at}: no stat block schema '{step.Schema}' in {system}/statblocks.");

                    foreach (var name in step.Validators.Where(n => !_validators.ContainsKey(n)))
                        problems.Add($"{at}: no validator named '{name}' is loaded (known: {string.Join(", ", _validators.Keys.Order(StringComparer.Ordinal))}).");
                }
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("Character creation recipes are invalid:\n  " + string.Join("\n  ", problems));
    }
}
