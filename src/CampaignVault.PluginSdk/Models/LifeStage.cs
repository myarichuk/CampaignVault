using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// Coarse life stage, set explicitly when a character is created or updated. Numeric ages don't
/// transfer across ancestries (a 90-year-old elf is young), so this is the stage the fiction means.
/// Content plugins gate on <see cref="LifeStageRules.IsAdult"/>; <see cref="Unspecified"/> is never adult.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LifeStage
{
    Unspecified = 0,
    Child = 1,
    Adolescent = 2,
    Adult = 3,
    Elder = 4
}

public static class LifeStageRules
{
    public static bool IsMinor(LifeStage stage) => stage is LifeStage.Child or LifeStage.Adolescent;

    public static bool IsAdult(LifeStage stage) => stage is LifeStage.Adult or LifeStage.Elder;

    /// <summary>
    /// Validates a life-stage write. A character recorded as a minor can never be relabelled adult
    /// through a commit — otherwise any gate on <see cref="IsAdult"/> is one character_update away from
    /// meaningless. A genuine in-world time skip gets a new character record instead.
    /// <see cref="LifeStage.Unspecified"/> as the requested value means "leave unchanged".
    /// </summary>
    public static bool TryChange(LifeStage current, LifeStage requested, out LifeStage result, out string? error)
    {
        error = null;
        result = current;
        if (requested == LifeStage.Unspecified || requested == current)
        {
            return true;
        }

        if (IsMinor(current) && !IsMinor(requested))
        {
            error = $"lifeStage is {current} and cannot be changed to {requested}. A character recorded as a minor " +
                    "stays one; if the story ages them across years, create a new character record.";
            return false;
        }

        result = requested;
        return true;
    }
}
