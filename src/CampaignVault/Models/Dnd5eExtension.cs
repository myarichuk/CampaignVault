using System.Text.Json.Serialization;

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

    public int GetAbilityModifier(int score)
    {
        return (int)Math.Floor((score - 10) / 2.0);
    }
}
