using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public record SpellSummaryView
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("concentration")]
    public bool Concentration { get; init; }

    [JsonPropertyName("castingTime")]
    public string? CastingTime { get; init; }
}

public record SpellListPaginationView
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

public record SpellListResponse
{
    [JsonPropertyName("system")]
    public string System { get; init; } = null!;

    [JsonPropertyName("class")]
    public string? Class { get; init; }

    [JsonPropertyName("filterLevel")]
    public int? FilterLevel { get; init; }

    [JsonPropertyName("spells")]
    public List<SpellSummaryView> Spells { get; init; } = [];

    [JsonPropertyName("pagination")]
    public SpellListPaginationView Pagination { get; init; } = null!;

    [JsonPropertyName("hint")]
    public string Hint { get; init; } = null!;
}