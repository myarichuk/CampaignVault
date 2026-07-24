using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure;

/// <summary>
/// Converts verbose low-severity pressure text to terse codes (e.g., "HUNGER", "QUEST:deadline:3d").
/// Only Suggestion-level pressures are abbreviated — higher severities always keep full text.
/// Unmatched text is NEVER abbreviated: better a verbose suggestion than one whose content is lost.
/// </summary>
internal static class PressureAbbreviator
{
    /// <summary>
    /// Generates a terse abbreviation for a Suggestion-level pressure item, or null to keep the
    /// full text (higher severities, blank text, or no recognized pattern).
    /// </summary>
    public static string? TryAbbreviate(WorldPressureItem item)
    {
        if (item.Severity != PressureSeverity.Suggestion)
            return null;

        if (string.IsNullOrWhiteSpace(item.Text))
            return null;

        var text = item.Text;
        var groupKey = item.GroupingKey ?? "";

        // Pattern matching: map common low-severity pressure text patterns to terse codes.
        // Format: {CODE} or {CODE}:{DETAIL} or {CODE}:{DETAIL}:{TIMEFRAME}

        if (text.Contains("starving", StringComparison.OrdinalIgnoreCase))
            return "HUNGER";

        if (text.Contains("parched", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("dying of thirst", StringComparison.OrdinalIgnoreCase))
            return "THIRST";

        if (text.Contains("exhausted", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("falling asleep", StringComparison.OrdinalIgnoreCase))
            return "TIRED";

        if (text.Contains("morale", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(text, @"morale[:\s]+(\d+)%", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var morale))
                return $"MORALE:{morale}%";
            return "MORALE:low";
        }

        if (text.Contains("wounded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blood loss", StringComparison.OrdinalIgnoreCase))
            return "WOUNDED";

        if (text.Contains("diseased", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("plague", StringComparison.OrdinalIgnoreCase))
            return "DISEASE";

        if (text.Contains("cursed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hexed", StringComparison.OrdinalIgnoreCase))
            return "CURSE";

        // Quest & factions: deadline or goal
        if (groupKey.Contains("Quest", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("quest", StringComparison.OrdinalIgnoreCase))
        {
            var daysMatch = Regex.Match(text, @"(\d+)\s*days?", RegexOptions.IgnoreCase);
            if (daysMatch.Success)
                return $"QUEST:deadline:{daysMatch.Groups[1].Value}d";
            return "QUEST:active";
        }

        if (text.Contains("faction", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("war", StringComparison.OrdinalIgnoreCase))
                return "FACTION:war";
            return "FACTION:stance";
        }

        // Rumors & intel
        if (groupKey.Contains("Rumor", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rumor", StringComparison.OrdinalIgnoreCase))
            return "RUMOR:spreading";

        // Travel & environment
        if (text.Contains("stuck", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("interrupted", StringComparison.OrdinalIgnoreCase))
            return "TRAVEL:blocked";

        if (text.Contains("exhaustion", StringComparison.OrdinalIgnoreCase))
        {
            var lvlMatch = Regex.Match(text, @"level[:\s]+(\d+)", RegexOptions.IgnoreCase);
            if (lvlMatch.Success)
                return $"EXHAUSTION:{lvlMatch.Groups[1].Value}";
            return "EXHAUSTION:pending";
        }

        if (text.Contains("temperature", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cold", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("heat", StringComparison.OrdinalIgnoreCase))
            return "CLIMATE";

        if (text.Contains("engagement", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("grapple", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("restrained", StringComparison.OrdinalIgnoreCase))
            return "ENGAGED:lock";

        // Location & scene issues
        if (text.Contains("hallucination", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("incoherent", StringComparison.OrdinalIgnoreCase))
            return "LOCATION:hallucination";

        if (text.Contains("Point of Interest", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PoI", StringComparison.Ordinal))
            return "POI:detail";

        if (text.Contains("ambient item", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("item expir", StringComparison.OrdinalIgnoreCase))
            return "ITEM:expiry";

        if (text.Contains("ambient crowd", StringComparison.OrdinalIgnoreCase))
            return "CROWD:refresh";

        // Memory & decay
        if (text.Contains("memory", StringComparison.OrdinalIgnoreCase))
            return "MEMORY:decay";

        // No recognized pattern: keep the full text — never degrade to a lossy generic code.
        return null;
    }
}
