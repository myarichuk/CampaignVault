namespace CampaignVault.Data;

/// <summary>
/// Normalizes entity ID prefixes to their canonical form at write and query-input boundaries.
/// The LLM sometimes supplies an equally-natural alias (e.g. "characters/" instead of "chars/")
/// or a bare slug with no prefix at all. Left un-normalized, these silently fail exact-match
/// comparisons/lookups deeper in the pipeline (e.g. ItemTransferHandler's "chars/"-prefix checks,
/// or a document load against a differently-prefixed ID that was never actually stored).
/// </summary>
internal static class CanonicalId
{
    public const string Characters = "chars/";
    public const string Locations = "locations/";
    public const string Items = "items/";
    public const string Factions = "factions/";
    public const string Quests = "quests/";
    public const string Rumors = "rumors/";
    public const string Lore = "lore/";
    public const string PlotThreads = "plot-threads/";
    public const string Spells = "spells/";
    public const string Feats = "feats/";
    public const string Creatures = "creatures/";
    public const string WorldEvents = "world-events/";

    private static readonly (string Alias, string Canonical)[] Aliases =
    [
        ("characters/", Characters),
    ];

    /// <summary>
    /// Rewrites known alias prefixes (e.g. "characters/" → "chars/") regardless of entity kind.
    /// Leaves an already-canonical ID, a bare ID, or a different entity kind's canonical prefix
    /// untouched. Safe to apply blanket across any ID-like field — it never invents a prefix that
    /// wasn't already implied by the alias.
    /// </summary>
    public static string NormalizeAlias(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return id ?? string.Empty;
        }

        foreach (var (alias, canonical) in Aliases)
        {
            if (id.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
            {
                return canonical + id[alias.Length..];
            }
        }

        return id;
    }

    /// <summary>
    /// Normalizes an ID to <paramref name="canonicalPrefix"/> for a write site that knows its own
    /// entity kind unambiguously (e.g. Character.Id inside UpsertCharacterAsync). Already-canonical
    /// IDs and known aliases are rewritten as in <see cref="NormalizeAlias"/>. A truly bare ID with
    /// no slash at all (e.g. "grog") has <paramref name="canonicalPrefix"/> prepended — the kind is
    /// unambiguous here, so this closes the ambiguity instead of leaving a malformed ID stored.
    /// Anything else that already contains a slash (a different known entity prefix like
    /// "locations/grog", or an arbitrary pre-existing convention this helper doesn't know about) is
    /// left untouched — coercing it could silently mask a real mismatch or mangle a valid ID into a
    /// double-prefixed string, which is worse than leaving the ambiguity in place.
    /// </summary>
    public static string Normalize(string? id, string canonicalPrefix)
    {
        if (string.IsNullOrEmpty(id))
        {
            return id ?? string.Empty;
        }

        if (id.StartsWith(canonicalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        var aliased = NormalizeAlias(id);
        if (aliased.StartsWith(canonicalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return aliased;
        }

        return id.Contains('/') ? id : canonicalPrefix + id;
    }
}
