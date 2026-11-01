using System;
using System.Collections.Generic;
using System.Linq;

namespace CampaignVault.Models;

/// <summary>
/// How far a table has chosen to take intimate scenes. It is a prompt-level setting: the server never talks to a model, so it
/// cannot enforce anything. It tells the client or model whether to carry intimacy to its threshold and cut away (the
/// default), or whether the table has opted past that on a model provider that permits it. Provider policy still decides
/// what is actually written, and nothing here ever relaxes the rule about children.
/// </summary>
public static class ContentLevels
{
    /// <summary>Romance, flirting and kisses are written; anything explicit is cut away. What an unset campaign gets.</summary>
    public const string Fade = "fade";

    /// <summary>The player chose to go beyond fade-to-black, on a provider that allows it.</summary>
    public const string Open = "open";

    /// <summary>
    /// The level in force: "open" while any adult mode is enabled (turning one on is the table's opt-in, and a campaign that had one
    /// on before this setting existed must keep behaving as it did), otherwise the stored choice. Null means the player has never chosen.
    /// </summary>
    public static string? Effective(string? stored, IEnumerable<string>? enabledModeIds, IReadOnlyCollection<string>? adultModeIds)
    {
        if (adultModeIds is { Count: > 0 } && enabledModeIds is not null
            && enabledModeIds.Any(m => adultModeIds.Contains(m, StringComparer.OrdinalIgnoreCase)))
        {
            return Open;
        }

        return Normalize(stored);
    }

    /// <summary>The canonical value for user input ("OPEN", " fade "), or null when it is neither.</summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is Fade or Open ? v : null;
    }
}
