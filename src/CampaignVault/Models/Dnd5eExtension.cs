using System.ComponentModel;
using System.Text.Json.Serialization;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Models;

public class Dnd5eExtension : SystemExtension
{
    [JsonPropertyName("armorClass")]
    public int ArmorClass { get; set; } = 10;

    // Core Abilities
    [JsonPropertyName("strength")]
    public int Strength { get; set; } = 10;
    
    [JsonPropertyName("dexterity")]
    public int Dexterity { get; set; } = 10;
    
    [JsonPropertyName("constitution")]
    public int Constitution { get; set; } = 10;
    
    [JsonPropertyName("intelligence")]
    public int Intelligence { get; set; } = 10;
    
    [JsonPropertyName("wisdom")]
    public int Wisdom { get; set; } = 10;
    
    [JsonPropertyName("charisma")]
    public int Charisma { get; set; } = 10;

    /// <summary>
    /// Total modifier for a given skill (e.g., "Stealth": 5, "Perception": 3).
    /// Used by the resolver to add bonuses to d20 rolls.
    /// </summary>
    [JsonPropertyName("skillModifiers")]
    public Dictionary<string, int> SkillModifiers { get; set; } = [];

    /// <summary>
    /// Total modifier for saving throws.
    /// </summary>
    [JsonPropertyName("savingThrowModifiers")]
    public Dictionary<string, int> SavingThrowModifiers { get; set; } = [];

    /// <summary>Hit die expression for HP derivation (e.g. "d12"). Used when maxHp is omitted on create.</summary>
    [JsonPropertyName("hitDie")]
    public string? HitDie { get; set; }

    /// <summary>Character level for HP/proficiency derivation. Parsed from classLevel when omitted.</summary>
    [JsonPropertyName("level")]
    public int? Level { get; set; }

    /// <summary>Whether to average or roll per-level HP gains when bootstrapping.</summary>
    [JsonPropertyName("hpMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HitPointDerivationMode? HpMode { get; set; }

    [Description("Multiclass levels on systemStats. Example: [{ class: Fighter, level: 5 }, { class: Wizard, level: 5 }]. Prefer over parsing classLevel string.")]
    [JsonPropertyName("classLevels")]
    public List<ClassLevelEntry> ClassLevels { get; set; } = [];

    [Description("Primary spellcasting ability: Intelligence, Wisdom, or Charisma. Inferred from classLevels when omitted.")]
    [JsonPropertyName("spellcastingAbility")]
    public string? SpellcastingAbility { get; set; }

    [Description("Spell save DC override. Omit to auto-derive (8 + proficiency + spellcasting ability mod) at bootstrap.")]
    [JsonPropertyName("spellSaveDc")]
    public int? SpellSaveDc { get; set; }

    [Description("Spell attack bonus override. Omit to auto-derive (proficiency + spellcasting ability mod) at bootstrap.")]
    [JsonPropertyName("spellAttackBonus")]
    public int? SpellAttackBonus { get; set; }

    public int GetAbilityModifier(int score)
    {
        return (int)Math.Floor((score - 10) / 2.0);
    }
}
