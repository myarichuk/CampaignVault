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
    public int Level { get; set; }

    /// <summary>Choice key from the progression data (e.g. "subclass", "fightingStyle", "asiOrFeat").</summary>
    public string Key { get; set; } = null!;

    /// <summary>The chosen option id, or free-text description for systems without an enumerated catalog.</summary>
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RulesetSystem System { get; set; }

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
