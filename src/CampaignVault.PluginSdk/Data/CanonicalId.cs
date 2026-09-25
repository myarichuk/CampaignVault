namespace CampaignVault.Data;

/// <summary>
/// Normalizes entity ID prefixes to their canonical form at write and query-input boundaries.
/// The LLM sometimes supplies an equally-natural alias (e.g. "characters/" instead of "chars/")
/// or a bare slug with no prefix at all. Left un-normalized, these silently fail exact-match
/// comparisons/lookups deeper in the pipeline (e.g. ItemTransferHandler's "chars/"-prefix checks,
/// or a document load against a differently-prefixed ID that was never actually stored).
/// </summary>
public static class CanonicalId
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

    private static readonly string[] AllCanonicalPrefixes =
    [
        Characters, Locations, Items, Factions, Quests, Rumors, Lore,
        PlotThreads, Spells, Feats, Creatures, WorldEvents
    ];

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
    /// An ID that already carries a *different* known canonical prefix (e.g. "chars/grog" passed in
    /// for an Item) is rejected rather than stored as-is: RavenDB document IDs are globally unique
    /// per database regardless of collection, so silently storing an Item under a Character's ID
    /// would overwrite that Character document at the storage layer — or, if the ID is already
    /// tracked under its real type in the same session, surface later as an opaque RavenDB
    /// "expected type X but got Y" crash deep in an unrelated query. An arbitrary slash-containing ID
    /// this helper doesn't recognize at all (some other convention) is still left untouched — only a
    /// collision with a *known* prefix is rejected.
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

        foreach (var known in AllCanonicalPrefixes)
        {
            if (!known.Equals(canonicalPrefix, StringComparison.OrdinalIgnoreCase)
                && aliased.StartsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"ID '{id}' carries the '{known}' prefix but is being stored as a '{canonicalPrefix}' entity. " +
                    "Refusing to store under a foreign-kind ID — this would collide with (and can silently overwrite) " +
                    $"an existing '{known}' document at that same ID. Use a '{canonicalPrefix}' ID instead.");
            }
        }

        return aliased.Contains('/') ? aliased : canonicalPrefix + aliased;
    }
}
