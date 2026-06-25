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
    public WorldPressureItem() : this(default!, default!, default!, default!) { }

    /// <summary>Optional machine-readable commit example(s) for the LLM to use.</summary>
    public string? SuggestedCommitJson { get; init; }

    public const string RumorsGroupingKey = "Simulation:Rumors";
    public const string SimulationEventGroupingKey = "Simulation:Event";
}
