using System.ComponentModel;
using System.Text.Json.Serialization;
using CampaignVault.Models;

namespace CampaignVault.Tools;

[Description("Batch of world changes for commit. Each change item must include a '$type' discriminator.")]
public class CommitRequest
{
    [Description("Array of world changes. Each item must be a JSON object with a '$type' discriminator (e.g., 'hp', 'activity', 'event'). Call get_help for change-type reference.")]
    [JsonPropertyName("changes")]
    public WorldChange[]? Changes { get; set; }

    [Description("Narrative summary of what happened (for the event log and world pressure).")]
    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }
}
