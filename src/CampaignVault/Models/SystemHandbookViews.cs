using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public record ClassHandbookEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("casterType")]
    public string CasterType { get; init; } = default!;

    [JsonPropertyName("hitDie")]
    public string? HitDie { get; init; }

    [JsonPropertyName("pools")]
    public List<string> Pools { get; init; } = [];
}

public record ConditionHandbookEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("durationType")]
    public string DurationType { get; init; } = default!;

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
    public string Hint { get; init; } = default!;
}

public record SystemHandbookResponse
{
    [JsonPropertyName("system")]
    public string System { get; init; } = default!;

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
    public string Notes { get; init; } = default!;
}