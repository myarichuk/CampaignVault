using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Campaign visibility rules for world entities and combatants.
/// Canon entities (null/empty <see cref="Character.CampaignName"/>) are visible in every campaign.
/// </summary>
public static class CampaignEntityVisibility
{
    public static bool IsVisibleInCampaign(string? entityCampaignName, string effectiveCampaign)
    {
        if (string.IsNullOrEmpty(entityCampaignName))
        {
            return true;
        }

        if (CampaignSlug.TryCanonicalize(entityCampaignName, out var entitySlug)
            && CampaignSlug.TryCanonicalize(effectiveCampaign, out var effectiveSlug))
        {
            return string.Equals(entitySlug, effectiveSlug, StringComparison.Ordinal);
        }

        return string.Equals(entityCampaignName, effectiveCampaign, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCombatantAllowed(Character character, string effectiveCampaign) =>
        character.CurrentHp > 0 && IsVisibleInCampaign(character.CampaignName, effectiveCampaign);

    public static bool IsPartyMember(Character character, string effectiveCampaign) =>
        !string.IsNullOrEmpty(character.CampaignName)
        && IsVisibleInCampaign(character.CampaignName, effectiveCampaign)
        && (character.IsPc || character.IsPartyCompanion);

    public static bool TryGetInvisibilityReason(Character character, string effectiveCampaign, out string reason)
    {
        if (IsVisibleInCampaign(character.CampaignName, effectiveCampaign))
        {
            reason = string.Empty;
            return false;
        }

        reason =
            $"Character '{character.Id}' belongs to campaign '{character.CampaignName}', not '{effectiveCampaign}'.";
        return true;
    }
}