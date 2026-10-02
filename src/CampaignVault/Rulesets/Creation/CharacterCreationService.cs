using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Creation;

/// <summary>The character builder's preview: the derived sheet plus every problem with the draft.</summary>
public sealed record CreationPreview(
    Character Character,
    IReadOnlyList<CreationIssue> Errors,
    IReadOnlyList<CreationIssue> Warnings,
    IReadOnlyList<string> Notes)
{
    /// <summary>The class features the character would have, its subclass's included (the sheet lists them).</summary>
    public IReadOnlyList<ClassFeatureView> ClassFeatures { get; init; } = [];
}

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
        DraftCharacterMapper.ToCharacter(draft, system, id, Steps(system, draft), _sources, _recipes.GetStatBlocksForSystem(system),
            new LevelChoicesApplied(
                Recipe(system).LevelSlots(draft.Kind, draft),
                Recipe(system).LevelPicks(draft.Kind, draft),
                Recipe(system).Increases(draft.Kind, draft),
                ClassOf(system, draft)));

    /// <summary>The draft's class as its progression names it, so the level choices recorded at creation say which class gave them.</summary>
    private string? ClassOf(string system, CharacterDraft draft) =>
        draft.GetList("class").FirstOrDefault() is { } id && _sources.ProgressionProvider.TryGetProgression(system, id, out var progression)
            ? progression.ClassName
            : null;

    /// <summary>What the draft's background starts the character with (items to give it on its first commit).</summary>
    public IReadOnlyList<StartingItem> StartingItems(string system, CharacterDraft draft) =>
        DraftCharacterMapper.Background(draft, system, Steps(system, draft), _sources)?.Equipment ?? [];

    /// <summary>The level choices the draft's class has at its level: every levelChoices step's slots, or one step's.</summary>
    public IReadOnlyList<LevelChoiceSlot> LevelSlots(string system, CharacterDraft draft, string? stepKey = null) =>
        Recipe(system).LevelSlots(draft.Kind, draft, stepKey);

    /// <summary>The psychology fields (<see cref="DraftCharacterMapper.PsychologyFields"/>) the draft's identity schemas edit, so committing owns those lists.</summary>
    public IReadOnlyCollection<string> PsychologyFields(string system, CharacterDraft draft)
    {
        var schemas = _recipes.GetStatBlocksForSystem(system);
        return
        [
            .. Steps(system, draft)
                .Where(s => s.Kind == CreationStepKinds.Identity && s.Schema is { } name && schemas.ContainsKey(name))
                .SelectMany(s => schemas[s.Schema!].Fields)
                .Select(f => f.Key)
                .Where(DraftCharacterMapper.IsPsychologyField)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

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

            // HP mode: the builder never rolls, so what it saves is what it previewed (level_up can still roll later).
            if (character.SystemStats is Dnd5eExtension { HpMode: null } && draft.Level > 1 && !character.IsPartyCompanion)
                notes.Add($"Hit points: the hit die's maximum at level 1, then its average (rounded up) plus Constitution for each of the other {draft.Level - 1} levels. The builder doesn't roll, so the character is saved with these hit points.");
        }
        else
        {
            notes.Add($"No ruleset module is loaded for '{system}', so nothing was derived.");
        }

        return new CreationPreview(
            character,
            [.. issues.Where(i => !i.IsWarning)],
            [.. issues.Where(i => i.IsWarning)],
            notes)
        {
            ClassFeatures = CharacterClassFeatures.Views(character, system, _sources.ProgressionProvider),
        };
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

                    foreach (var type in step.ChoiceTypes.Where(t => !Enum.TryParse<ChoiceType>(t, ignoreCase: true, out _)))
                        problems.Add($"{at}: unknown choice type '{type}' (use {string.Join(", ", Enum.GetNames<ChoiceType>())}).");
                    if (step.ChoiceTypes.Count > 0 && step.Kind != CreationStepKinds.LevelChoices)
                        problems.Add($"{at}: choiceTypes is for levelChoices steps.");

                    foreach (var name in step.Validators.Where(n => !_validators.ContainsKey(n)))
                        problems.Add($"{at}: no validator named '{name}' is loaded (known: {string.Join(", ", _validators.Keys.Order(StringComparer.Ordinal))}).");
                }
            }
        }

        foreach (var system in _recipes.Systems)
        {
            // Every stat block field has to land somewhere: a stats field of that name, the notes, or the psychology. A field that lands
            // nowhere would be dropped on commit.
            var stats = SystemStatsMerger.CreateDefault(system);
            foreach (var (name, schema) in _recipes.GetStatBlocksForSystem(system))
            {
                foreach (var field in schema.Fields.Where(f => !DraftCharacterMapper.IsNotesField(f.Key) && !DraftCharacterMapper.IsPsychologyField(f.Key) && !DraftCharacterMapper.HasStatsField(stats, f.Key)))
                    problems.Add($"{system} stat block '{name}': field '{field.Key}' has no stats field to write to (the {stats.GetType().Name}).");
                foreach (var field in schema.Fields.Where(f => !StatBlockField.Types.Contains(f.Type)))
                    problems.Add($"{system} stat block '{name}': field '{field.Key}' has unknown type '{field.Type}' (use {string.Join(", ", StatBlockField.Types)}).");
                foreach (var field in schema.Fields.Where(f => f.Type is "modifiers" or "choice" && CreationSources.FieldNames(system, f.Source).Count == 0))
                    problems.Add($"{system} stat block '{name}': {field.Type} field '{field.Key}' needs a source with names in {system} (skills, abilities, saves, creatureTypes).");
                foreach (var field in schema.Fields.Where(f => f.Type == "rows"))
                {
                    if (field.Columns is not { Count: > 0 })
                        problems.Add($"{system} stat block '{name}': rows field '{field.Key}' needs columns.");
                    foreach (var column in field.Columns ?? [])
                    {
                        if (!StatBlockColumn.Types.Contains(column.Type))
                            problems.Add($"{system} stat block '{name}': column '{field.Key}.{column.Key}' has unknown type '{column.Type}' ({string.Join(", ", StatBlockColumn.Types)}).");
                    }
                }
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("Character creation recipes are invalid:\n  " + string.Join("\n  ", problems));
    }
}
