using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// The one definition of "is concentrating". A status concentrates when it carries the marker (a
/// <c>conditionName</c> of "concentration", or a "Concentration" category). Statuses stored before the marker existed
/// are matched by name as a fallback ("Concentration", "Concentration: Bless", "Concentration (Bless)"); that name
/// match is deprecated. "Lost Concentration" or "Concentration Broken" are not concentration.
/// </summary>
public static partial class ConcentrationRules
{
    public const string Marker = "concentration";

    [GeneratedRegex(@"^\s*concentration\s*([:(\-–—]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyName();

    public static bool IsConcentration(StatusEffect effect) =>
        string.Equals(effect.ConditionName, Marker, StringComparison.OrdinalIgnoreCase)
        || string.Equals(effect.Category, "Concentration", StringComparison.OrdinalIgnoreCase)
        || (effect.Name is { } name && LegacyName().IsMatch(name));

    public static StatusEffect? Find(IEnumerable<StatusEffect>? effects) => effects?.FirstOrDefault(IsConcentration);
}
