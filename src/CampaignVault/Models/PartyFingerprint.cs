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

    /// <summary>True when both fingerprints name the same members at the same locations (HP may differ).</summary>
    public static bool SameLocations(string a, string b) => Locations(a) == Locations(b);

    private static string Locations(string fingerprint) =>
        string.Join(",", fingerprint.Split(',').Select(e =>
        {
            var at = e.LastIndexOf('@');
            var colon = e.IndexOf(':');
            return colon < 0 || at < colon ? e : e[..colon] + e[at..];
        }));
}
