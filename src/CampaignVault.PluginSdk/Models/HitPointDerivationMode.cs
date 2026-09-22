using System.Text.Json.Serialization;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>HP derivation mode for level-up / bootstrap WorldChanges (Raven-free; lives in PluginSdk).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HitPointDerivationMode
{
    Average,
    Rolled
}
