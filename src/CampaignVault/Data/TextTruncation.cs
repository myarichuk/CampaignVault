namespace CampaignVault.Data;

/// <summary>
/// Caps free-text fields (Location.Description, PointOfInterestDetails values, Character.Notes) at the
/// wire-projection boundary — the underlying document keeps the full text; only what goes out over MCP
/// is shortened. See CampaignConfig.LocationDescriptionCharCap and friends for the configured caps.
/// </summary>
public static class TextTruncation
{
    /// <summary>
    /// Returns (text, truncated). If text is null/empty or already within maxChars, returns it unchanged
    /// with truncated=false. Otherwise cuts at the last whitespace at or before maxChars (falling back to
    /// a hard cut if there's no whitespace at all, e.g. one giant token) and appends an ellipsis, so the
    /// result never ends mid-word.
    /// </summary>
    public static (string? Text, bool Truncated) TruncateAtBoundary(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars || maxChars <= 0)
        {
            return (text, false);
        }

        var cut = text.LastIndexOf(' ', maxChars - 1);
        var end = cut > 0 ? cut : maxChars;
        return (text[..end].TrimEnd() + "…", true);
    }
}
