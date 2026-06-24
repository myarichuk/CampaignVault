namespace CampaignVault.Data;

/// <summary>
/// Validation for <see cref="Models.Character.IsPc"/> and <see cref="Models.Character.IsPartyCompanion"/>.
/// </summary>
public static class CharacterPartyRules
{
    public static bool TryValidate(bool isPc, bool isPartyCompanion, string? campaignName, out string? error)
    {
        if (isPc && isPartyCompanion)
        {
            error = "A character cannot be both isPc and isPartyCompanion.";
            return false;
        }

        if ((isPc || isPartyCompanion) && string.IsNullOrWhiteSpace(campaignName))
        {
            error =
                "isPc and isPartyCompanion require a campaign slug (set CampaignName on the entity). " +
                "Shared canon NPCs must leave both flags false.";
            return false;
        }

        error = null;
        return true;
    }
}