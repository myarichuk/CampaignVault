using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public record ItemDefinitionSummaryView
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object> Properties { get; init; } = [];
}

public record ItemDefinitionListPaginationView
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

public record ItemDefinitionListResponse
{
    [JsonPropertyName("system")]
    public string System { get; init; } = null!;

    [JsonPropertyName("items")]
    public List<ItemDefinitionSummaryView> Items { get; init; } = [];

    [JsonPropertyName("pagination")]
    public ItemDefinitionListPaginationView Pagination { get; init; } = null!;

    [JsonPropertyName("hint")]
    public string Hint { get; init; } = null!;
}
