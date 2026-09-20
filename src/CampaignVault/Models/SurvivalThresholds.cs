namespace CampaignVault.Models;

/// <summary>
/// Thresholds shared between <see cref="Data.SurvivalDeprivationRule"/> (which decides when
/// sustained hunger/thirst/temperature exposure escalates into the ruleset's exhaustion-equivalent
/// condition) and the pressure contributors that narrate the same signals, so the two never drift
/// apart. Modeled after <see cref="NpcMoodThresholds"/>.
/// </summary>
public static class SurvivalThresholds
{
    public const float SevereHunger = 90f;
    public const float RecoveryHunger = 20f;

    public const float SevereThirst = 90f;
    public const float RecoveryThirst = 20f;

    /// <summary>
    /// Felt temperature (°C) at/below which exposure is life-threatening. Also the threshold
    /// <see cref="Data.Pressure.Contributors.CharacterDistressPressureContributor"/> uses for its
    /// "freezing to death" pressure — kept as one constant so they can't disagree.
    /// </summary>
    public const float SevereCold = -20f;

    /// <summary>
    /// Felt temperature (°C) at/above which exposure is life-threatening. Also the threshold
    /// <see cref="Data.Pressure.Contributors.CharacterDistressPressureContributor"/> uses for its
    /// "extreme heat" pressure.
    /// </summary>
    public const float SevereHeat = 50f;

    /// <summary>
    /// Margin back inside the safe band required before a temperature deprivation streak resets,
    /// so a reading sitting exactly on the extreme boundary doesn't flap the streak every tick.
    /// </summary>
    public const float TemperatureRecoveryMargin = 5f;
}
