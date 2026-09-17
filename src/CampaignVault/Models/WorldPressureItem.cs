namespace CampaignVault.Models;

public enum PressureSeverity
{
    Suggestion = 0,
    Simulation = 1,
    NarrativePrompt = 2,
    EngineWarning = 3
}

/// <summary>
/// Structured object representing a pressure/nag before it is capped and deduplicated.
/// </summary>
public record WorldPressureItem(
    PressureSeverity Severity,
    string EntityId,
    string Text,
    string GroupingKey)
{
    public WorldPressureItem() : this(default!, null!, null!, null!) { }

    /// <summary>Optional machine-readable commit example(s) for the LLM to use.</summary>
    public string? SuggestedCommitJson { get; init; }

    /// <summary>Terse abbreviation for this pressure (e.g. "HUNGER", "QUEST:deadline:3d"). Only set for Suggestion-level items with a recognized pattern; null means Text is used as-is.</summary>
    public string? Abbreviation { get; init; }

    /// <summary>
    /// Optional display name for EntityId, when the contributor already has it on hand (e.g. Character.Name).
    /// Lets batched-display formatting (see PressureManager.ToDisplayStrings) show "{EntityName} (detail)"
    /// instead of repeating each item's full boilerplate Text — without having to parse the name back out
    /// of free text. Null means batching falls back to full Text for this item.
    /// </summary>
    public string? EntityName { get; init; }

    public const string RumorsGroupingKey = "Simulation:Rumors";
    public const string SimulationEventGroupingKey = "Simulation:Event";
}
