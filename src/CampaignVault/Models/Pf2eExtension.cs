using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public class Pf2eExtension : SystemExtension
{
    [JsonPropertyName("armorClass")]
    public int ArmorClass { get; set; } = 10;
    
    // PF2e typically just tracks the modifier directly, but we can store both
    [JsonPropertyName("strengthMod")]
    public int StrengthMod { get; set; } = 0;
    
    [JsonPropertyName("dexterityMod")]
    public int DexterityMod { get; set; } = 0;
    
    [JsonPropertyName("constitutionMod")]
    public int ConstitutionMod { get; set; } = 0;
    
    [JsonPropertyName("intelligenceMod")]
    public int IntelligenceMod { get; set; } = 0;
    
    [JsonPropertyName("wisdomMod")]
    public int WisdomMod { get; set; } = 0;
    
    [JsonPropertyName("charismaMod")]
    public int CharismaMod { get; set; } = 0;

    [JsonPropertyName("skillModifiers")]
    public Dictionary<string, int> SkillModifiers { get; set; } = [];
    
    [JsonPropertyName("savingThrowModifiers")]
    public Dictionary<string, int> SavingThrowModifiers { get; set; } = [];

    [JsonPropertyName("classHpPerLevel")]
    public int? ClassHpPerLevel { get; set; }

    [JsonPropertyName("ancestryHp")]
    public int? AncestryHp { get; set; }

    [JsonPropertyName("level")]
    public int? Level { get; set; }
}
