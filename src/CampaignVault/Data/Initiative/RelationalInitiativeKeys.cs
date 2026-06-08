namespace CampaignVault.Data.Initiative;

/// <summary>
/// Stable initiative key suffixes for persistent relationship-band signals.
/// Shared by <see cref="RelationalInitiativeProvider"/> and <see cref="RelationalRearmRule"/>.
/// </summary>
internal static class RelationalInitiativeKeys
{
    public static string? TryGetPersistentKey(string npcId, string targetId, int relationshipValue)
    {
        if (relationshipValue >= 60)
        {
            return $"affection:{npcId}:{targetId}";
        }

        if (relationshipValue <= -60)
        {
            return $"resentment:{npcId}:{targetId}";
        }

        if (relationshipValue >= 40)
        {
            return $"trust:{npcId}:{targetId}";
        }

        return null;
    }
}