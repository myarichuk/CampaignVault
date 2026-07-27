using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Shared logic for stamping a race/ancestry template's Size and Traits onto a Character's
/// DistinctiveFeatures, used by both <see cref="Dnd5eDeriveRaceStep"/> and <see cref="Pf2eDeriveAncestryStep"/>.
/// </summary>
internal static class RaceTraitStamper
{
    public static void StampSizeAndTraits(Character character, string? size, IReadOnlyList<string> traits)
    {
        var features = new List<string>(character.DistinctiveFeatures);

        if (!string.IsNullOrWhiteSpace(size))
        {
            var sizeFeature = $"Size: {size}";
            if (!features.Contains(sizeFeature))
            {
                features.Add(sizeFeature);
            }
        }

        foreach (var trait in traits)
        {
            if (!features.Contains(trait))
            {
                features.Add(trait);
            }
        }

        character.DistinctiveFeatures = features;
    }
}
