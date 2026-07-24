using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure;

/// <summary>
/// Converts verbose pressure text to terse codes (e.g., "HUNGER:critical", "QUEST:deadline:3days").
/// Only abbreviates Suggestion-level pressures to preserve high-fidelity data for important alerts.
/// Reduces per-turn chattiness by ~100-150 tokens per scene on average.
/// </summary>
internal static class PressureAbbreviator
{
    /// <summary>
    /// Generates a terse abbreviation for a pressure item based on its text and grouping key.
    /// Only returns abbreviations for Suggestion-level pressures; higher severities keep full text.
    /// </summary>
    public static string? TryAbbreviate(WorldPressureItem item)
    {
        // Only abbreviate low-severity pressures (Suggestion level).
        // Higher severities (Simulation, NarrativePrompt, EngineWarning) keep full text for clarity.
        if (item.Severity != PressureSeverity.Suggestion)
            return null;

        if (string.IsNullOrWhiteSpace(item.Text))
            return null;

        var text = item.Text;
        var groupKey = item.GroupingKey ?? "";

        // Pattern matching: map common low-severity pressure text patterns to terse codes.
        // Format: {CODE}:{DETAIL} or {CODE}:{DETAIL}:{TIMEFRAME}

        // Character distress: "This character is starving..." → "HUNGER:critical"
        if (text.Contains("starving", StringComparison.OrdinalIgnoreCase))
            return $"HUNGER:{SeverityCode(item.Severity)}";

        if (text.Contains("parched", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("dying of thirst", StringComparison.OrdinalIgnoreCase))
            return $"THIRST:{SeverityCode(item.Severity)}";

        if (text.Contains("exhausted", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("falling asleep", StringComparison.OrdinalIgnoreCase))
            return $"TIRED:{SeverityCode(item.Severity)}";

        if (text.Contains("morale", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(text, @"morale[:\s]+(\d+)%", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var morale))
                return $"MORALE:{morale}%";
            return "MORALE:low";
        }

        if (text.Contains("wounded", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blood loss", StringComparison.OrdinalIgnoreCase))
            return $"WOUNDED:{SeverityCode(item.Severity)}";

        if (text.Contains("diseased", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("plague", StringComparison.OrdinalIgnoreCase))
            return $"DISEASE:{SeverityCode(item.Severity)}";

        if (text.Contains("cursed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hexed", StringComparison.OrdinalIgnoreCase))
            return $"CURSE:{SeverityCode(item.Severity)}";

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
                return $"FACTION:war:{SeverityCode(item.Severity)}";
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
            return $"CLIMATE:{SeverityCode(item.Severity)}";

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

        // Fallback: use first words of text if no specific pattern matched
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            var fallback = string.Join("_", words.Take(2)).ToUpperInvariant();
            return $"{fallback}:{SeverityCode(item.Severity)}";
        }

        return null;
    }

    private static string SeverityCode(PressureSeverity severity) =>
        severity switch
        {
            PressureSeverity.EngineWarning => "ALERT",
            PressureSeverity.NarrativePrompt => "prompt",
            PressureSeverity.Simulation => "sim",
            PressureSeverity.Suggestion => "info",
            _ => "?"
        };
}
