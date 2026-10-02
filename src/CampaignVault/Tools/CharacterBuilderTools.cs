using System.ComponentModel;
using System.Text.RegularExpressions;
using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

/// <summary>What <c>character_builder</c> returns; each action fills its own part.</summary>
public sealed record CharacterBuilderResult
{
    public string System { get; init; } = null!;
    public string Kind { get; init; } = null!;

    /// <summary>steps: the recipe's steps for this draft, in order.</summary>
    public IReadOnlyList<CreationStep>? Steps { get; init; }

    /// <summary>
    /// steps: for each step key, the steps its choice depends on (a heritage reads the ancestry, trained skills the
    /// class, background and boosts). Changing one of those clears it and refetches its options.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Reads { get; init; }

    /// <summary>steps: the stat block schemas identity steps name.</summary>
    public IReadOnlyList<StatBlockView>? StatBlocks { get; init; }

    /// <summary>steps: the highest level the recipe builds at, when it has one.</summary>
    public int? MaxLevel { get; init; }

    /// <summary>options: what the step offers.</summary>
    public IReadOnlyList<CreationOption>? Options { get; init; }

    /// <summary>options: how many picks the step wants, when fixed.</summary>
    public int? Count { get; init; }

    /// <summary>options for a spells step: picks per group (cantrips, known, prepared).</summary>
    public IReadOnlyDictionary<string, int>? GroupCounts { get; init; }

    /// <summary>options for a level choices step: its slots in level order; each option's group is its slot's id.</summary>
    public IReadOnlyList<LevelChoiceSlot>? Slots { get; init; }

    /// <summary>preview / commit: the derived sheet.</summary>
    public CharacterDetailView? Character { get; init; }

    /// <summary>preview: the class features the character would have, its subclass's included.</summary>
    public IReadOnlyList<Rulesets.ClassFeatureView>? ClassFeatures { get; init; }

    public IReadOnlyList<CreationIssue> Errors { get; init; } = [];
    public IReadOnlyList<CreationIssue> Warnings { get; init; } = [];

    /// <summary>preview / commit: what the engine derived.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record StatBlockView(string Name, IReadOnlyList<StatBlockField> Fields, string? Title = null);

/// <summary>
/// The character builder: the client walks a system's recipe step by step (steps → options → preview) and commits
/// once. Commit goes through world_build, so a built character is exactly what world_build would make.
/// </summary>
[McpServerToolType]
public partial class CharacterBuilderTools : CampaignToolBase, IMcpServerTool
{
    private readonly CharacterCreationService _creation;
    private readonly WorldBuilderTools _worldBuilder;
    private readonly ItemDefinitionProvider? _items;

    public CharacterBuilderTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        CharacterCreationService creation,
        CharacterBootstrapOrchestrator bootstrap,
        ResourcePoolInitializer? poolInitializer = null,
        ILogger<CharacterBuilderTools>? logger = null,
        ItemDefinitionProvider? items = null)
        : base(repository, keys, logger)
    {
        _creation = creation;
        _items = items;
        // The same world_build path the model uses, so commit can't drift from it.
        _worldBuilder = new WorldBuilderTools(repository, keys, bootstrap, poolInitializer);
    }

    [ToolCategory("World builder")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("Character builder for player characters and companions, driven by the campaign ruleset's recipe. " +
                 "action=steps: the steps for draft.kind (pc|companion) and draft.level. action=options: what step offers for this draft. " +
                 "action=preview: the derived sheet plus errors/warnings; nothing is saved. action=commit: creates the character via world_build " +
                 "(errors block it); send the returned id as draft.id to update instead of duplicating. " +
                 "draft.choices is keyed by step key: a string (pickOne), a string list (pickN), {method, scores} (abilityScores), {cantrips, known, prepared} (spells), " +
                 "{\"<level>.<choice>\": id or [ids]} (levelChoices; an ability score improvement is one ability for +2, two for +1 each, or one feat).")]
    public Task<ToolResult<CharacterBuilderResult>> CharacterBuilder(
        [Description("steps | options | preview | commit")]
        string action,
        [Description("The draft: { id?, kind: pc|companion, level, name, concept, look, choices: { <stepKey>: value } }.")]
        CharacterDraft draft,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("options only: the step key.")]
        string? step = null)
    {
        draft ??= new CharacterDraft();
        return (action ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "steps" => Read(campaignName, draft, system => StepsResult(system, draft)),
            "options" => Read(campaignName, draft, system => OptionsResult(system, draft, step)),
            "preview" => PreviewAsync(campaignName, draft),
            "commit" => CommitAsync(campaignName, draft),
            _ => Task.FromResult(new ToolResult<CharacterBuilderResult>(
                false, Error: ToolErrors.InvalidArgument, Summary: $"Unknown action '{action}'. Use steps, options, preview or commit.")),
        };
    }

    private Task<ToolResult<CharacterBuilderResult>> Read(
        string campaignName, CharacterDraft draft, Func<string, CharacterBuilderResult> read) =>
        ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var system = await SystemAsync(s, effective, draft);
            var result = read(system);
            return new ToolResult<CharacterBuilderResult>(true, result, $"{system} {result.Kind} builder.");
        }, saveChanges: false);

    private CharacterBuilderResult StepsResult(string system, CharacterDraft draft)
    {
        var steps = _creation.Steps(system, draft);
        var allSteps = _creation.Recipe(system).AllSteps(draft.Kind);
        var reads = steps.ToDictionary(s => s.Key, s => RecipeCharacterCreation.Reads(s, allSteps), StringComparer.OrdinalIgnoreCase);
        var schemas = allSteps
            .Select(s => s.Schema)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => _creation.StatBlock(system, name))
            .OfType<StatBlockSchema>()
            .Select(s => new StatBlockView(s.Name, [.. s.Fields.Select(f => f.Type is "modifiers" or "choice" ? f with { Keys = CreationSources.FieldNames(system, f.Source) } : f)], s.Title))
            .ToList();
        return new CharacterBuilderResult
        {
            System = system,
            Kind = draft.Kind,
            Steps = steps,
            Reads = reads,
            StatBlocks = schemas.Count > 0 ? schemas : null,
            MaxLevel = _creation.Recipe(system).MaxLevel(draft.Kind),
        };
    }

    private CharacterBuilderResult OptionsResult(string system, CharacterDraft draft, string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(stepKey))
            throw new ArgumentException("options needs step: the step key (from action=steps).");

        var options = _creation.Options(system, stepKey, draft);
        var recipeStep = _creation.Recipe(system).AllSteps(draft.Kind).First(s => s.Key.Equals(stepKey, StringComparison.OrdinalIgnoreCase));
        var ctx = _creation.Context(system, draft);
        var groupCounts = recipeStep.Kind == CreationStepKinds.Spells
            ? new Dictionary<string, int>
            {
                [SpellGroups.Cantrips] = ctx.Count(recipeStep, SpellGroups.Cantrips) ?? 0,
                [SpellGroups.Known] = ctx.Count(recipeStep, SpellGroups.Known) ?? 0,
                [SpellGroups.Prepared] = ctx.Count(recipeStep, SpellGroups.Prepared) ?? 0,
            }
            : null;
        return new CharacterBuilderResult
        {
            System = system,
            Kind = draft.Kind,
            Options = options,
            Count = groupCounts is null ? ctx.Count(recipeStep, null) : null,
            GroupCounts = groupCounts,
            Slots = recipeStep.Kind == CreationStepKinds.LevelChoices ? _creation.LevelSlots(system, draft, recipeStep.Key) : null,
        };
    }

    private Task<ToolResult<CharacterBuilderResult>> PreviewAsync(string campaignName, CharacterDraft draft) =>
        ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var system = await SystemAsync(s, effective, draft);
            var preview = await _creation.PreviewAsync(system, draft);
            var result = new CharacterBuilderResult
            {
                System = system,
                Kind = draft.Kind,
                Character = CharacterDetailView.From(preview.Character),
                ClassFeatures = preview.ClassFeatures,
                Errors = preview.Errors,
                Warnings = preview.Warnings,
                Notes = preview.Notes,
            };
            var summary = preview.Errors.Count == 0
                ? "Preview ready; the draft can be committed."
                : $"Preview with {preview.Errors.Count} error(s); fix them before commit.";
            return new ToolResult<CharacterBuilderResult>(true, result, summary);
        }, saveChanges: false);

    private async Task<ToolResult<CharacterBuilderResult>> CommitAsync(string campaignName, CharacterDraft draft)
    {
        // Validate and build the request in a read-only session; world_build then writes in its own.
        CharacterUpsertRequest? request = null;
        List<ItemUpsertRequest> items = [];
        var prepared = await ExecuteForCampaignAsync(campaignName, async (effective, s) =>
        {
            var system = await SystemAsync(s, effective, draft);
            var id = string.IsNullOrWhiteSpace(draft.Id) ? NewId(draft.Name) : draft.Id.Trim();
            var preview = await _creation.PreviewAsync(system, draft, id);
            var result = new CharacterBuilderResult
            {
                System = system,
                Kind = draft.Kind,
                Errors = preview.Errors,
                Warnings = preview.Warnings,
            };
            if (preview.Errors.Count > 0)
            {
                return new ToolResult<CharacterBuilderResult>(false, result,
                    $"Not committed: {string.Join(" ", preview.Errors.Select(e => $"[{e.Step}] {e.Message}"))}",
                    ToolErrors.InvalidArgument);
            }

            // A new character goes in raw, so world_build derives it exactly as it would any new character. Committing
            // to an existing id sends the already-derived sheet: world_build's update path doesn't re-add racial bonuses.
            var existing = await s.LoadAsync<Character>(id);
            var exists = existing is not null;
            var character = exists ? preview.Character : _creation.ToCharacter(system, draft, id);
            request = new CharacterUpsertRequest
            {
                Id = id,
                Name = character.Name,
                ClassLevel = character.ClassLevel,
                Notes = character.Notes,
                CurrentAppearance = character.CurrentAppearance,
                IsPc = character.IsPc,
                IsPartyCompanion = character.IsPartyCompanion,
                MaxHp = exists ? character.MaxHp : 0,
                CurrentHp = exists ? character.CurrentHp : 0,
                SystemStats = character.SystemStats,
                Psychology = Psychology(existing, character, _creation.PsychologyFields(system, draft)),
            };
            if (!exists)
                items = StartingItems(system, id, _creation.StartingItems(system, draft));
            return new ToolResult<CharacterBuilderResult>(true, result);
        }, saveChanges: false);

        if (!prepared.Success || request is null)
            return prepared;

        var built = await _worldBuilder.WorldBuild(
            new WorldBuildBatch { Characters = [request], Items = items.Count > 0 ? items : null }, campaignName);
        if (!built.Success)
            return new ToolResult<CharacterBuilderResult>(false, prepared.Data, built.Summary, built.Error);

        var stored = await ExecuteForCampaignAsync(campaignName, async (_, s) =>
        {
            var character = await s.LoadAsync<Character>(request.Id);
            return new ToolResult<CharacterBuilderResult>(true, prepared.Data! with
            {
                Character = character is null ? null : CharacterDetailView.From(character),
                Notes = [.. built.Data?.Warnings ?? []],
            }, $"Committed {request.Name} as {request.Id}. {built.Summary}");
        }, saveChanges: false);
        return stored;
    }

    /// <summary>
    /// A new character's starting items, held by it: an item template's by its id (world_build copies the template's
    /// fields in, and its description here), or a plain named item.
    /// </summary>
    private List<ItemUpsertRequest> StartingItems(string system, string characterId, IReadOnlyList<StartingItem> equipment)
    {
        var slug = characterId.Split('/').Last();
        var items = new List<ItemUpsertRequest>();
        foreach (var (entry, index) in equipment.Select((e, i) => (e, i)))
        {
            var name = entry.Name ?? (entry.Item is { } id ? CreationSources.Label(id) : null);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var template = entry.Item is { } item && _items?.TryGet(system, item, out var found) == true ? found : null;
            items.Add(new ItemUpsertRequest
            {
                Id = $"items/{slug}-start-{index + 1}",
                Name = name,
                Description = template?.Description ?? name,
                HolderId = characterId,
                Quantity = Math.Max(1, entry.Quantity),
                DefinitionName = entry.Item,
            });
        }

        return items;
    }

    /// <summary>
    /// The psychology to send: null (world_build keeps what's there) when the recipe has no psychology fields; else the
    /// built lists, and on a character that exists, its own profile (memories, mood) with only those lists replaced.
    /// </summary>
    private static PsychologyProfile? Psychology(Character? existing, Character built, IReadOnlyCollection<string> fields)
    {
        if (fields.Count == 0)
            return null;
        if (existing is null)
            return built.Psychology;

        var profile = existing.Psychology;
        foreach (var field in fields)
        {
            var list = DraftCharacterMapper.Psychology(existing, field)!;
            list.Clear();
            list.AddRange(DraftCharacterMapper.Psychology(built, field)!);
        }

        return profile;
    }

    private async Task<string> SystemAsync(Raven.Client.Documents.Session.IAsyncDocumentSession s, string campaign, CharacterDraft draft)
    {
        var config = await s.LoadAsync<CampaignConfig>(_keys.Config(campaign));
        var active = config?.ActiveSystem;
        if (string.IsNullOrWhiteSpace(active))
        {
            // Characters can be built during onboarding, before finalize writes the campaign's config: play the system chosen there.
            var onboarding = await s.LoadAsync<OnboardingState>(_keys.StateOnboarding(campaign));
            if (onboarding?.CollectedAnswers.TryGetValue(OnboardingQuestionCatalog.System, out var chosen) == true
                && !string.IsNullOrWhiteSpace(chosen?.ToString()))
            {
                active = chosen.ToString();
            }
        }

        var system = RulesetSystem.Canonicalize(active ?? RulesetSystem.Dnd5e);
        if (!string.IsNullOrWhiteSpace(draft.System) && !RulesetSystem.Canonicalize(draft.System).Equals(system, StringComparison.Ordinal))
            throw new ArgumentException($"The draft is for '{draft.System}', but campaign '{campaign}' plays {system}.");

        return system;
    }

    private static string NewId(string? name)
    {
        var slug = NonSlug().Replace((name ?? "character").Trim().ToLowerInvariant(), "-").Trim('-');
        return $"{CanonicalId.Characters}{(slug.Length == 0 ? "character" : slug)}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
