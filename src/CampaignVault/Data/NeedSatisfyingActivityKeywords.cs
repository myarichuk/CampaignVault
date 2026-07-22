namespace CampaignVault.Data;

/// <summary>
/// Keywords identifying when a character's CurrentActivity already addresses a given need — shared by
/// <see cref="ScheduleNeedSatisfactionRule"/> (settles scheduled NPCs' needs toward baseline when their
/// routine matches) and <see cref="Initiative.NeedActivityConflictHelper.EvaluateWant"/> (suppresses an
/// initiative "want" candidate when the NPC is already doing something about it — no point surfacing
/// "wants to nap" for someone who is currently resting).
/// </summary>
public static class NeedSatisfyingActivityKeywords
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["hunger"] = ["eat", "meal", "dine", "food", "feast", "breakfast", "lunch", "dinner"],
        ["thirst"] = ["drink", "water", "beverage", "ale", "wine"],
        ["tiredness"] = ["sleep", "rest", "nap", "bed", "camp"],
        ["social_drive"] = ["tavern", "socialize", "gather", "party", "celebrate", "mingle"],
    };

    public static bool IsSatisfying(string need, string? activity)
    {
        if (string.IsNullOrWhiteSpace(activity) || !Map.TryGetValue(need, out var keywords))
        {
            return false;
        }

        return keywords.Any(k => activity.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
