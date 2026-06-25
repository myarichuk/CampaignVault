using System.Text.Json.Serialization;
using CampaignVault.Models;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets.Bootstrap;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HitPointDerivationMode
{
    Average,
    Rolled
}

public enum BootstrapTrigger
{
    Create,
    Upsert,
    LevelUp,
    SystemStatsPatch
}

public sealed class BootstrapContext
{
    public required Character Character { get; init; }
    public required RulesetSystem ActiveSystem { get; init; }
    public BootstrapTrigger Trigger { get; init; } = BootstrapTrigger.Create;
    public int? ExplicitMaxHp { get; init; }
    public int? ExplicitCurrentHp { get; init; }
    public int LevelsGained { get; init; } = 1;
    /// <summary>When leveling up a multiclass PC, the class that gained the level (e.g. "Wizard").</summary>
    public string? ClassGained { get; init; }
    public HitPointDerivationMode? HpModeOverride { get; init; }
    public IAsyncDocumentSession? Session { get; init; }
    public string? CampaignName { get; init; }

    public bool HasExplicitMaxHp => ExplicitMaxHp is > 0;
}

public sealed class BootstrapStepResult
{
    public required string StepName { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> LlmHints { get; init; } = [];
}

public sealed class BootstrapReport
{
    public IReadOnlyList<BootstrapStepResult> Steps { get; init; } = [];
    public IReadOnlyList<string> Messages =>
        Steps.Select(s => s.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Cast<string>().ToList();
    public IReadOnlyList<string> LlmHints =>
        Steps.SelectMany(s => s.LlmHints).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
}