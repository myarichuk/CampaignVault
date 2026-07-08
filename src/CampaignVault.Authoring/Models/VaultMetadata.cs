using System;
using System.Collections.Generic;

namespace CampaignVault.Authoring.Models;

public class VaultMetadata
{
    public int SchemaVersion { get; set; } = 1;

    public string CampaignName { get; set; } = string.Empty;

    public string? Ruleset { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Human-friendly display name for the campaign.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Narrative focus tags to guide LLM story direction (e.g., "political intrigue", "dungeon crawl").
    /// </summary>
    public List<string> NarrativeFocus { get; set; } = [];
}