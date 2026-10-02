using System.Text.Json.Serialization;
using CampaignVault.Data.Templates;

namespace CampaignVault.Models;

/// <summary>
/// A single level-up choice applied to a character, kept as a permanent history entry on
/// <see cref="SystemExtension.LevelUpChoices"/>. Repeatable choices (feats gained at several
/// levels) each get their own entry instead of overwriting one another.
/// </summary>
public class LevelUpChoiceRecord
{
    /// <summary>The character level at which this choice was made.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>Choice key from the progression data (e.g. "subclass", "fightingStyle", "asiOrFeat").</summary>
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public string Key { get; set; } = null!;

    /// <summary>The chosen option id, or free-text description for systems without an enumerated catalog.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public string Value { get; set; } = null!;
}

/// <summary>
/// A single pending choice returned by the level-up guidance tool, describing what the DM should
/// ask the player before committing a <see cref="LevelUpChange"/>.
/// </summary>
public class PendingLevelUpChoice
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = null!;

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("type")]
    public ChoiceType Type { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    [JsonPropertyName("options")]
    public List<ChoiceOption> Options { get; set; } = [];

    [JsonPropertyName("abilityOptions")]
    public List<string> AbilityOptions { get; set; } = [];

    /// <summary>How many different options to take (two metamagic options at once). Record each as its own choice.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;
}

/// <summary>
/// Response from the read-only pending-choices lookup. Purely informational — no session state is
/// kept server-side. The DM-LLM should converse with the player about these choices, then commit a
/// single <see cref="LevelUpChange"/> with the answers in <c>choices</c>/<c>abilityScoreIncreases</c>.
/// </summary>
public class PendingLevelUpChoicesResponse
{
    public string CharacterId { get; set; } = null!;
    public string ClassName { get; set; } = null!;
    public int CurrentLevel { get; set; }
    public int TargetLevel { get; set; }

    public string? System { get; set; }

    /// <summary>Feature names/descriptions gained at the target level, for narrative flavor.</summary>
    public List<string> Features { get; set; } = [];

    /// <summary>Enumerated choices to ask the player about (5e classes with authored progression data).</summary>
    public List<PendingLevelUpChoice> Choices { get; set; } = [];

    /// <summary>
    /// PF2e-only: feat/boost counts pending at this level. PF2e progression data tracks counts, not an
    /// enumerated feat catalog, so the DM should ask the player to name their picks free-text and pass
    /// them via <c>choices</c> with a "classFeat"/"skillFeat"/"generalFeat"/"ancestryFeat" key.
    /// </summary>
    public Pf2eLevelBudget? Pf2eBudget { get; set; }

    /// <summary>
    /// The same choices as <see cref="Choices"/> in the shape a menu is built from, with the PF2e attribute boosts and skill
    /// increases and the 5e ability score improvement's feats spelled out. Commit the answers as <c>picks</c> (slot id → options).
    /// </summary>
    public List<PendingLevelUpSlot> Slots { get; set; } = [];

    public string Summary { get; set; } = null!;
}

public class Pf2eLevelBudget
{
    public int ClassFeats { get; set; }
    public int SkillFeats { get; set; }
    public int GeneralFeats { get; set; }
    public int AncestryFeats { get; set; }
    public int AbilityBoosts { get; set; }
}

/// <summary>One choice of the level being gained, ready to ask: <c>4.asiOrFeat</c>, <c>3.subclass</c>, <c>5.skillIncrease</c>.</summary>
public class PendingLevelUpSlot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("title")]
    public string Title { get; set; } = null!;

    /// <summary>Enum, AsiOrFeat, AttributeBoosts, SkillIncrease, SkillProficiency, FeatSelection or FreeText.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>How many options to pick. An ability score improvement is one ability (+2) or two (+1 each), or one feat.</summary>
    [JsonPropertyName("picks")]
    public int Picks { get; set; } = 1;

    [JsonPropertyName("abilities")]
    public List<string>? Abilities { get; set; }

    [JsonPropertyName("options")]
    public List<PendingLevelUpOption> Options { get; set; } = [];
}

public class PendingLevelUpOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("label")]
    public string Label { get; set; } = null!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("homebrew")]
    public bool Homebrew { get; set; }
}

/// <summary>
/// Whether a character can gain a level now, as the sheet shows it. <see cref="Ready"/> is the XP rule: the campaign's
/// XP table (or custom thresholds) says the character has enough. Milestone campaigns are never ready by XP; the DM or
/// the player decides, so a player character can always ask for the menu while <see cref="Possible"/>.
/// </summary>
public class LevelUpStatus
{
    [JsonPropertyName("possible")]
    public bool Possible { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("targetLevel")]
    public int TargetLevel { get; set; }

    [JsonPropertyName("xp")]
    public int Xp { get; set; }

    /// <summary>The XP the target level needs, or null for milestone campaigns and systems with no XP table.</summary>
    [JsonPropertyName("xpNeeded")]
    public int? XpNeeded { get; set; }
}
