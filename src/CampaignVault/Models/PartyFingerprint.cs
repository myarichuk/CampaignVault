namespace CampaignVault.Models;

/// <summary>
/// P2-10(b): the deliberately narrow, readable party fingerprint ("charId:hp/maxHp@locationId", sorted by
/// id, comma-joined). Shared by take_turn and start_session so a kickoff fingerprint echoes cleanly into
/// the first take_turn's clientPartyFingerprint.
/// </summary>
public static class PartyFingerprint
{
    public static string Compute(IEnumerable<Character> party) =>
        string.Join(",", party
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => $"{c.Id}:{c.CurrentHp}/{c.MaxHp}@{c.CurrentLocationId ?? "?"}"));
}
