using System.Text;

namespace CampaignVault.Data;

/// <summary>
/// Single source of truth for campaign slug normalization used in document keys,
/// session selection, and entity <see cref="Models.Character.CampaignName"/> tagging.
/// </summary>
public static class CampaignSlug
{
    /// <summary>
    /// Canonicalizes a campaign name/slug: trim, lowercase, separators to hyphen, collapse repeats.
    /// </summary>
    public static string Canonicalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Campaign name cannot be empty.", nameof(input));
        }

        var sb = new StringBuilder(input.Length);
        var previousWasSeparator = false;

        foreach (var ch in input.Trim())
        {
            if (ch is ' ' or '_' or '/' or '\\' or '-')
            {
                if (sb.Length > 0 && !previousWasSeparator)
                {
                    sb.Append('-');
                    previousWasSeparator = true;
                }

                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
            previousWasSeparator = false;
        }

        var result = sb.ToString().Trim('-');
        if (result.Length == 0)
        {
            throw new ArgumentException("Campaign name cannot be empty after normalization.", nameof(input));
        }

        return result;
    }

    /// <summary>
    /// Attempts to canonicalize; returns false when input is null/whitespace or normalizes to empty.
    /// </summary>
    public static bool TryCanonicalize(string? input, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            slug = Canonicalize(input);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}