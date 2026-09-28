using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// A link from a subject to an anchor (0.10.0): a horse hitched to a post, a captive on a rope held by a rider, a wrist
/// tied to a bed frame. Mechanical meaning only: the subject cannot travel unless the anchor (or whoever holds it)
/// travels along, and can strain against <see cref="BreakDc"/>. No physics: everything else is narration.
/// A tether ends by <c>tether detach</c>, a successful strain, the anchor being destroyed or archived, or its holder
/// being incapacitated.
/// </summary>
public record Tether
{
    /// <summary>A character, an item, or a fixture written as <c>fixture:hitching-post</c> that lives only in the fiction.</summary>
    [JsonPropertyName("anchorId")]
    public string AnchorId { get; init; } = null!;

    /// <summary>Check total needed to break free.</summary>
    [JsonPropertyName("breakDc")]
    public int BreakDc { get; init; } = 15;

    /// <summary>How far the subject can move from the anchor, in feet. Null = no stated slack (held tight).</summary>
    [JsonPropertyName("slackFeet")]
    public int? SlackFeet { get; init; }

    /// <summary>Who is holding the anchor end, when it is a character. Their incapacitation releases the tether.</summary>
    [JsonPropertyName("holderId")]
    public string? HolderId { get; init; }

    /// <summary>What it is, for the DM: "lead rope", "frog tie to the bedpost".</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Set by whoever attached it (a plugin id, a character id), for audit.</summary>
    [JsonPropertyName("attachedBy")]
    public string? AttachedBy { get; init; }
}
