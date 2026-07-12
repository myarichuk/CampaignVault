using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public record CreatureSummaryView
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = default!;

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("isHomebrew")]
    public bool IsHomebrew { get; init; }

    [JsonPropertyName("level")]
    public int? Level { get; init; }

    [JsonPropertyName("challengeRating")]
    public string? ChallengeRating { get; init; }

    [JsonPropertyName("hp")]
    public int? Hp { get; init; }

    [JsonPropertyName("defense")]
    public int? Defense { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("skills")]
    public List<string> Skills { get; init; } = [];

    [JsonPropertyName("abilities")]
    public List<string> Abilities { get; init; } = [];
}

public record CreatureListPaginationView
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

public record CreatureListResponse
{
    [JsonPropertyName("system")]
    public string System { get; init; } = default!;

    [JsonPropertyName("creatures")]
    public List<CreatureSummaryView> Creatures { get; init; } = [];

    [JsonPropertyName("pagination")]
    public CreatureListPaginationView Pagination { get; init; } = default!;

    [JsonPropertyName("hint")]
    public string Hint { get; init; } = default!;
}
