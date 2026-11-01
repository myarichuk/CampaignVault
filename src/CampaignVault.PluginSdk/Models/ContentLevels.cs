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

    /// <summary>The canonical value for user input ("OPEN", " fade "), or null when it is neither.</summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is Fade or Open ? v : null;
    }
}
