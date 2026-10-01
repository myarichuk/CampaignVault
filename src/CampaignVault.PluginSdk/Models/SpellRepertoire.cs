using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// The spells a caster has: cantrips, spells known (or a wizard's spellbook) and today's prepared list. Names are
/// spell template names. Slots are a separate resource pool; this is only which spells.
/// </summary>
public class SpellRepertoire
{
    [JsonPropertyName("cantrips")]
    public List<string> Cantrips { get; set; } = [];

    [Description("Spells known (known casters) or the spellbook (wizard).")]
    [JsonPropertyName("known")]
    public List<string> Known { get; set; } = [];

    [Description("Spells prepared today (prepared casters). Empty for known casters.")]
    [JsonPropertyName("prepared")]
    public List<string> Prepared { get; set; } = [];
}
