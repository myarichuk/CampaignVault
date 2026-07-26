using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public record ClassHandbookEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    [JsonPropertyName("casterType")]
    public string CasterType { get; init; } = null!;

    [JsonPropertyName("hitDie")]
    public string? HitDie { get; init; }

    [JsonPropertyName("pools")]
    public List<string> Pools { get; init; } = [];
}

public record ConditionHandbookEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    [JsonPropertyName("durationType")]
    public string DurationType { get; init; } = null!;

    [JsonPropertyName("mechanicalSummary")]
    public string? MechanicalSummary { get; init; }

    [JsonPropertyName("moodHint")]
    public string? MoodHint { get; init; }
}

public record CreatureHandbookSummary
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("exampleNames")]
    public List<string> ExampleNames { get; init; } = [];

    [JsonPropertyName("hint")]
    public string Hint { get; init; } = null!;
}

public record SystemHandbookResponse
{
    [JsonPropertyName("system")]
    public string System { get; init; } = null!;

    [JsonPropertyName("classes")]
    public List<ClassHandbookEntry> Classes { get; init; } = [];

    [JsonPropertyName("races")]
    public List<string> Races { get; init; } = [];

    [JsonPropertyName("backgrounds")]
    public List<string> Backgrounds { get; init; } = [];

    [JsonPropertyName("feats")]
    public List<string> Feats { get; init; } = [];

    [JsonPropertyName("conditions")]
    public List<ConditionHandbookEntry> Conditions { get; init; } = [];

    [JsonPropertyName("creatures")]
    public CreatureHandbookSummary? Creatures { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = null!;
}