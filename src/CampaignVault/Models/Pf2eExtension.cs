using System.ComponentModel;
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

    [Description("Ancestry template name (e.g. \"human\", \"elf\", \"dwarf\"). References an AncestryDefinition template in RulesetData/pf2e/ancestries/.")]
    [JsonPropertyName("ancestry")]
    public string? Ancestry { get; set; }

    [Description("Heritage template name (e.g. \"half-elf\", \"dwarven_clan_drinker\"). References a HeritageDefinition template in RulesetData/pf2e/heritages/.")]
    [JsonPropertyName("heritage")]
    public string? Heritage { get; set; }

    [Description("Background template name (e.g. \"soldier\", \"scholar\", \"criminal\"). References a BackgroundDefinition template in RulesetData/pf2e/backgrounds/.")]
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [Description("Ancestry feat template names. References FeatDefinition templates in RulesetData/pf2e/feats/.")]
    [JsonPropertyName("ancestryFeats")]
    public List<string> AncestryFeats { get; set; } = [];

    [Description("Class feat template names. References FeatDefinition templates in RulesetData/pf2e/feats/.")]
    [JsonPropertyName("classFeats")]
    public List<string> ClassFeats { get; set; } = [];

    [Description("Skill feat template names. References FeatDefinition templates in RulesetData/pf2e/feats/.")]
    [JsonPropertyName("skillFeats")]
    public List<string> SkillFeats { get; set; } = [];

    [Description("General feat template names. References FeatDefinition templates in RulesetData/pf2e/feats/.")]
    [JsonPropertyName("generalFeats")]
    public List<string> GeneralFeats { get; set; } = [];
}
