using CampaignVault.Models;

namespace CampaignVault.Data.Initiative;

public static class DispositionMatcher
{
    public static (int FearHits, int WantHits, float DispositionStress) Score(
        PsychologyProfile psychology,
        IReadOnlyList<Character> presentEntities,
        Location? location,
        CampaignConfig config)
    {
        var sceneTokens = BuildSceneTokens(presentEntities, location, config.DispositionMinTokenLength);
        var fearHits = CountMatches(psychology.Fears, sceneTokens, config, config.DispositionMinTokenLength);
        var wantHits = CountMatches(psychology.Wants, sceneTokens, config, config.DispositionMinTokenLength);

        var anxiousTraits = psychology.Traits.Count(t =>
            t.Equals("anxious", StringComparison.OrdinalIgnoreCase)
            || t.Equals("timid", StringComparison.OrdinalIgnoreCase)
            || t.Equals("paranoid", StringComparison.OrdinalIgnoreCase));

        var baseScore = fearHits * 25f - wantHits * 10f;
        var traitMult = 1.0f + (0.15f * anxiousTraits);
        var dispositionStress = Math.Clamp(baseScore * traitMult, 0f, 100f);

        return (fearHits, wantHits, dispositionStress);
    }

    public static IReadOnlyList<string> GetMatchedFears(
        PsychologyProfile psychology,
        IReadOnlyList<Character> presentEntities,
        Location? location,
        CampaignConfig config)
    {
        var sceneTokens = BuildSceneTokens(presentEntities, location, config.DispositionMinTokenLength);
        return GetMatchedPhrases(psychology.Fears, sceneTokens, config, config.DispositionMinTokenLength);
    }

    private static List<string> GetMatchedPhrases(
        IEnumerable<string> phrases,
        HashSet<string> sceneTokens,
        CampaignConfig config,
        int minTokenLength)
    {
        var matched = new List<string>();
        foreach (var phrase in phrases)
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                continue;
            }

            var keywords = ExpandKeywords(phrase, config, minTokenLength);
            if (keywords.Any(k => sceneTokens.Any(s => TokensMatch(k, s, minTokenLength))))
            {
                matched.Add(phrase);
            }
        }

        return matched;
    }

    internal static HashSet<string> BuildSceneTokens(
        IReadOnlyList<Character> presentEntities,
        Location? location,
        int minTokenLength)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (location != null)
        {
            foreach (var tag in location.VisualTags ?? [])
            {
                AddToken(tokens, tag, minTokenLength);
            }

            if (!string.IsNullOrWhiteSpace(location.AmbientCrowd))
            {
                foreach (var token in Tokenize(location.AmbientCrowd, minTokenLength))
                {
                    tokens.Add(token);
                }
            }
        }

        foreach (var entity in presentEntities)
        {
            foreach (var tag in entity.VisualTags ?? [])
            {
                AddToken(tokens, tag, minTokenLength);
            }

            foreach (var token in Tokenize(entity.Name, minTokenLength))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static int CountMatches(
        IEnumerable<string> phrases,
        HashSet<string> sceneTokens,
        CampaignConfig config,
        int minTokenLength)
    {
        var hits = 0;
        foreach (var phrase in phrases)
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                continue;
            }

            var keywords = ExpandKeywords(phrase, config, minTokenLength);
            if (keywords.Any(k => sceneTokens.Any(s => TokensMatch(k, s, minTokenLength))))
            {
                hits++;
            }
        }

        return hits;
    }

    internal static IEnumerable<string> ExpandKeywords(string phrase, CampaignConfig config, int minTokenLength)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(phrase, minTokenLength))
        {
            keywords.Add(token);

            if (config.DispositionKeywordExpansions.TryGetValue(token, out var synonyms))
            {
                foreach (var synonym in synonyms)
                {
                    AddToken(keywords, synonym, minTokenLength);
                }
            }
        }

        return keywords;
    }

    internal static bool TokensMatch(string left, string right, int minTokenLength)
    {
        if (left.Length < minTokenLength || right.Length < minTokenLength)
        {
            return false;
        }

        return right.Contains(left, StringComparison.OrdinalIgnoreCase)
               || left.Contains(right, StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> Tokenize(string text, int minTokenLength)
    {
        foreach (var raw in text.Split([' ', ',', '.', ';', ':', '-', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length >= minTokenLength)
            {
                yield return token;
            }
        }
    }

    private static void AddToken(ISet<string> tokens, string value, int minTokenLength)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length >= minTokenLength)
        {
            tokens.Add(normalized);
        }
    }
}