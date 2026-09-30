namespace CampaignVault.Models;

/// <summary>
/// 5e death-save progress of a player character at 0 HP. Null on everyone who is not currently dying, so existing
/// documents load unchanged. Started when damage drops a 5e PC to 0 HP, advanced by the <c>death_save</c> verb and by
/// further damage while at 0 HP, and cleared by any healing above 0 HP or by death.
/// </summary>
public class DeathSaveTally
{
    public int Successes { get; set; }

    public int Failures { get; set; }

    /// <summary>Three successes: unconscious at 0 HP but no longer rolling. Damage makes them unstable again.</summary>
    public bool Stable { get; set; }
}
