namespace CampaignVault.Services;

public readonly record struct NameSearchHit<T>(T Item, int Score);

/// <summary>
/// In-memory ranked name index for template catalogs (spells, items, creatures).
/// Names are tokenised into a vocabulary with posting lists; a query token expands to the vocabulary
/// tokens it matches (exact, prefix, plural/possessive, substring, or a small typo), and a name is a hit
/// only when <em>every</em> query token matched — so "tiny hut" never drags in unrelated names.
/// Hits come back best-first.
/// </summary>
public sealed class NameSearchIndex<T>
{
    private const int PhraseBonus = 30;
    private const int ExactNameBonus = 50;

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, List<int>> _postings = new(StringComparer.Ordinal);

    private NameSearchIndex()
    {
    }

    public int Count => _entries.Count;

    public static NameSearchIndex<T> Build(IEnumerable<T> items, Func<T, string> nameSelector)
    {
        var index = new NameSearchIndex<T>();
        foreach (var item in items)
        {
            var tokens = NameSearchText.Tokenize(nameSelector(item));
            var id = index._entries.Count;
            index._entries.Add(new Entry(item, string.Join(' ', tokens), tokens.Count));

            foreach (var token in tokens.Distinct(StringComparer.Ordinal))
            {
                if (!index._postings.TryGetValue(token, out var list))
                {
                    list = [];
                    index._postings[token] = list;
                }

                list.Add(id);
            }
        }

        return index;
    }

    public IReadOnlyList<NameSearchHit<T>> Search(string? query)
    {
        var queryTokens = NameSearchText.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (queryTokens.Count == 0)
        {
            return [];
        }

        // Best score per (query token, entry); an entry must be present for every query token.
        Dictionary<int, int>? running = null;
        foreach (var queryToken in queryTokens)
        {
            var perEntry = new Dictionary<int, int>();
            foreach (var (vocabToken, ids) in _postings)
            {
                var score = NameSearchText.ScoreToken(queryToken, vocabToken);
                if (score <= 0)
                {
                    continue;
                }

                foreach (var id in ids)
                {
                    if (!perEntry.TryGetValue(id, out var best) || score > best)
                    {
                        perEntry[id] = score;
                    }
                }
            }

            if (running == null)
            {
                running = perEntry;
                continue;
            }

            foreach (var id in running.Keys.ToList())
            {
                if (perEntry.TryGetValue(id, out var score))
                {
                    running[id] += score;
                }
                else
                {
                    running.Remove(id);
                }
            }
        }

        var normalizedQuery = string.Join(' ', queryTokens);
        var hits = new List<NameSearchHit<T>>(running!.Count);
        foreach (var (id, sum) in running)
        {
            var entry = _entries[id];
            var total = sum;
            if (entry.Normalized == normalizedQuery)
            {
                total += ExactNameBonus;
            }
            else if (queryTokens.Count > 1 && entry.Normalized.Contains(normalizedQuery, StringComparison.Ordinal))
            {
                total += PhraseBonus;
            }

            // Prefer names with fewer words the query did not ask for.
            total -= Math.Max(0, entry.TokenCount - queryTokens.Count) * 3;
            hits.Add(new NameSearchHit<T>(entry.Item, total));
        }

        return hits;
    }

    private readonly record struct Entry(T Item, string Normalized, int TokenCount);
}

/// <summary>Tokenisation and per-token scoring shared by every <see cref="NameSearchIndex{T}"/>.</summary>
internal static class NameSearchText
{
    private const int ExactScore = 100;
    private const int PrefixScore = 80;
    private const int LongerQueryScore = 60;
    private const int SubstringScore = 50;
    private const int TypoOneScore = 60;
    private const int TypoTwoScore = 45;

    /// <summary>
    /// Lower-cases, drops apostrophes, splits on every other non-alphanumeric character (so slugs like
    /// <c>tiny_hut</c> and names like "Leomund's Tiny Hut" tokenise alike), and folds a plural/possessive
    /// trailing "s" so "leomunds"/"leomund" and "huts"/"hut" agree.
    /// </summary>
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(Stem(current.ToString()));
            current.Clear();
        }

        foreach (var c in text)
        {
            if (c is '\'' or '’')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;
    }

    private static string Stem(string token) =>
        token.Length > 3 && token[^1] == 's' && token[^2] != 's' ? token[..^1] : token;

    /// <summary>Score of one query token against one vocabulary token; 0 = no match.</summary>
    public static int ScoreToken(string query, string vocab)
    {
        if (query == vocab)
        {
            return ExactScore;
        }

        if (query.Length >= 2 && vocab.StartsWith(query, StringComparison.Ordinal))
        {
            return PrefixScore;
        }

        if (vocab.Length >= 3 && query.Length - vocab.Length is > 0 and <= 2
            && query.StartsWith(vocab, StringComparison.Ordinal))
        {
            return LongerQueryScore;
        }

        if (query.Length >= 4 && vocab.Contains(query, StringComparison.Ordinal))
        {
            return SubstringScore;
        }

        // Typo tolerance only for tokens long enough that one or two edits are unlikely to hit a different word.
        var shorter = Math.Min(query.Length, vocab.Length);
        if (shorter < 4)
        {
            return 0;
        }

        var maxDistance = shorter >= 7 ? 2 : 1;
        if (Math.Abs(query.Length - vocab.Length) > maxDistance)
        {
            return 0;
        }

        var distance = EditDistance(query, vocab, maxDistance);
        return distance switch
        {
            1 => TypoOneScore,
            2 => TypoTwoScore,
            _ => 0,
        };
    }

    /// <summary>Optimal-string-alignment distance (insert/delete/substitute/transpose); returns int.MaxValue when it exceeds <paramref name="max"/>.</summary>
    private static int EditDistance(string a, string b, int max)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length] > max ? int.MaxValue : d[a.Length, b.Length];
    }
}
