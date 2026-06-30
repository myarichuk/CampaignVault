using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public class Fallout2d20Extension : SystemExtension
{
    [JsonPropertyName("strength")]
    public int Strength { get; set; } = 5;
    
    [JsonPropertyName("perception")]
    public int Perception { get; set; } = 5;
    
    [JsonPropertyName("endurance")]
    public int Endurance { get; set; } = 5;
    
    [JsonPropertyName("charisma")]
    public int Charisma { get; set; } = 5;
    
    [JsonPropertyName("intelligence")]
    public int Intelligence { get; set; } = 5;
    
    [JsonPropertyName("agility")]
    public int Agility { get; set; } = 5;
    
    [JsonPropertyName("luck")]
    public int Luck { get; set; } = 5;

    [JsonPropertyName("skills")]
    public Dictionary<string, int> Skills { get; set; } = [];

    [JsonPropertyName("tagSkills")]
    public List<string> TagSkills { get; set; } = [];

    [JsonPropertyName("defense")]
    public int Defense { get; set; } = 1;

    [JsonPropertyName("damageResistance")]
    public Dictionary<string, int> DamageResistance { get; set; } = []; 

    [JsonPropertyName("hungerRateMultiplier")]
    public float HungerRateMultiplier { get; set; } = 1.0f;
    
    [JsonPropertyName("thirstRateMultiplier")]
    public float ThirstRateMultiplier { get; set; } = 1.0f;

    [JsonPropertyName("level")]
    public int? Level { get; set; }

    /// <summary>HP gained per level after L1. Defaults to Endurance when bootstrapping.</summary>
    [JsonPropertyName("hpPerLevel")]
    public int? HpPerLevel { get; set; }

    [JsonPropertyName("perks")]
    public List<string> Perks { get; set; } = [];
}
