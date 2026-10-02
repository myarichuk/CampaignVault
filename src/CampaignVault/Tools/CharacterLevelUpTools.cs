using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Rulesets.Creation;
using CampaignVault.Services;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

/// <summary>What <c>character_level_up</c> returns.</summary>
public sealed record CharacterLevelUpResult
{
    public string CharacterId { get; init; } = null!;
    public string Name { get; init; } = null!;
    public string? ClassName { get; init; }
    public LevelUpStatus? Status { get; init; }

    /// <summary>options: what the class gains at the target level.</summary>
    public IReadOnlyList<ClassFeatureView> Features { get; init; } = [];

    /// <summary>options: the choices of that level, in the builder's shape.</summary>
    public IReadOnlyList<PendingLevelUpSlot> Slots { get; init; } = [];

    /// <summary>apply: what the level did, as the commit reports it.</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];

    /// <summary>apply: the character after the level.</summary>
    public CharacterDetailView? Character { get; init; }
}

/// <summary>
/// The level-up menu's tool: the game client asks what the next level offers (<c>options</c>) and sends the player's picks
/// (<c>apply</c>), which are committed as an ordinary <c>level_up</c> change, so a menu and a model-driven level are the
/// same code path. Not listed to models (they use lookup kind=level_up and the level_up change).
/// </summary>
[McpServerToolType]
public sealed class CharacterLevelUpTools(
    CampaignRepository repository,
    CampaignDocumentKeys keys,
    LevelUpPlanner planner,
    ProgressionDefinitionProvider progressions,
    ILogger<CharacterLevelUpTools>? logger = null) : CampaignToolBase(repository, keys, logger), IMcpServerTool
{
    private readonly CampaignRepository _repository = repository;
    private readonly CampaignDocumentKeys _keys = keys;

    [ToolCategory("Campaign management")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("Level-up menu for a player character or companion. action=options: whether the next level is available (and earned by XP), " +
                 "what it gives and the choices it asks (slots). action=apply: gain the level with the player's picks (slot id → option ids), " +
                 "checked and applied like a level_up change; refused whole with the reason if a pick isn't allowed.")]
    public Task<ToolResult<CharacterLevelUpResult>> CharacterLevelUp(
        [Description("options | apply")] string action,
        [Description("Character id, e.g. 'chars/hero-123'.")] string characterId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName,
        [Description("apply only: slot id → the option ids picked, as options listed them.")] Dictionary<string, List<string>>? picks = null,
        [Description("apply only (5e): average or rolled hit points for the level.")] string? hpMode = null)
    {
        var apply = string.Equals(action?.Trim(), "apply", StringComparison.OrdinalIgnoreCase);
        if (!apply && !string.Equals(action?.Trim(), "options", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ToolResult<CharacterLevelUpResult>(
                false, Error: ToolErrors.InvalidArgument, Summary: $"Unknown action '{action}'. Use options or apply."));
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var campaign = new CampaignSession(session, effective);
            var character = await _repository.GetCharacterAsync(campaign, characterId);
            var config = await session.LoadAsync<CampaignConfig>(_keys.Config(effective));
            if (character is null || config is null)
            {
                return new ToolResult<CharacterLevelUpResult>(false, Error: "CharacterNotFound", Summary: $"Character {characterId} not found.");
            }

            var status = LevelUpStatusReader.For(character, config, progressions);
            if (status is not { Possible: true })
            {
                return new ToolResult<CharacterLevelUpResult>(
                    false, Error: ToolErrors.InvalidArgument,
                    Summary: $"{character.Name} can't gain a level: only player characters and companions with a class below level {LevelUpStatusReader.MaxLevel} can.");
            }

            var plan = planner.Plan(character, config.ActiveSystem);
            var name = character.Name;
            if (!apply)
            {
                var picked = CharacterClassFeatures.Picked(character.SystemStats, multiclass: false);
                var features = plan is null
                    ? []
                    : CharacterClassFeatures.Classes(character, config.ActiveSystem, progressions)
                        .SelectMany(c => c.Progression.FeaturesUpTo(plan.ClassLevel, picked).Where(f => f.Level == plan.ClassLevel))
                        .Select(f => CharacterClassFeatures.View(f, plan.ClassLevel))
                        .ToList();
                return new ToolResult<CharacterLevelUpResult>(true, new CharacterLevelUpResult
                {
                    CharacterId = characterId,
                    Name = name,
                    ClassName = plan?.ClassName,
                    Status = status,
                    Features = features,
                    Slots = [.. (plan?.Slots ?? []).Select(LevelUpPlanner.Describe)],
                }, $"{name} would reach level {status.TargetLevel}" + (plan is null ? " (no authored progression: nothing to choose)." : $" with {plan.Slots.Count} choice(s)."));
            }

            // The menu asks for everything the level requires; only a model-written level_up may skip a choice (with a warning).
            if (plan is not null && planner.Validate(plan, character, picks ?? []) is { Count: > 0 } problems)
            {
                return new ToolResult<CharacterLevelUpResult>(
                    false, Error: "ValidationError", Summary: $"No level was gained. {string.Join(" ", problems)}");
            }

            var change = new LevelUpChange
            {
                CharacterId = character.Id,
                LevelsGained = 1,
                HealToMatch = false,
                ClassGained = plan?.ClassName,
                Picks = picks is { Count: > 0 } ? picks : null,
                HpMode = Enum.TryParse<HitPointDerivationMode>(hpMode, true, out var mode) ? mode : null,
                Reason = status.Ready ? $"Reached the XP for level {status.TargetLevel}" : "Chosen at the level-up menu",
            };
            var result = await _repository.StageChangesAsync(campaign, [change]);
            if (!result.Success)
            {
                return new ToolResult<CharacterLevelUpResult>(
                    false, Error: "ValidationError", Summary: $"No level was gained. {string.Join(" ", result.Summary)}");
            }

            var after = await _repository.GetCharacterAsync(campaign, characterId);
            return new ToolResult<CharacterLevelUpResult>(true, new CharacterLevelUpResult
            {
                CharacterId = characterId,
                Name = name,
                ClassName = plan?.ClassName,
                Status = after is null ? null : LevelUpStatusReader.For(after, config, progressions),
                Applied = result.Summary,
                Character = after is null ? null : CharacterDetailView.From(after),
            }, string.Join(" ", result.Summary));
        });
    }
}
