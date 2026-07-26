using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// One class and its level for multiclass characters. Prefer this structured form over parsing freeform classLevel strings.
/// </summary>
public class ClassLevelEntry
{
    [JsonPropertyName("class")]
    public string Class { get; set; } = null!;

    [JsonPropertyName("level")]
    public int Level { get; set; }
}