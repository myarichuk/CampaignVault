using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampaignVault.Tests;

/// <summary>
/// Deterministic, dependency-free approximation of how many BPE tokens a serialized MCP response
/// costs in an LLM's context.
///
/// WHY NOT A REAL TOKENIZER: a real BPE vocabulary is a multi-megabyte download, which would make the
/// test suite need the network and pin the numbers to one specific model's vocab. What the budget
/// gates actually need is a measure that is (a) stable across runs and machines, and (b) monotonic in
/// the thing we care about — if a change adds a field to every NPC in a scene, the number must go up.
/// Both hold here.
///
/// WHY NOT chars/4: that is the constant this harness replaces. JSON is not prose — it is dense in
/// punctuation and short keys, exactly where chars/4 is worst. `{"hp":12,"maxHp":24}` is 20 chars
/// (chars/4 = 5) but really about 13 tokens. Undercounting by ~2.5x on the densest part of the payload
/// is how a "small" response shape quietly becomes an expensive one.
///
/// HOW IT APPROXIMATES: BPE splits text into runs and merges within them, so the estimate does the
/// same, per run class:
///   - letter runs   : ceil(len/4)  — common short words land on 1 token, long identifiers split
///                     ("characterId" -> ~3), which is what real vocabularies do.
///   - digit runs    : ceil(len/3)  — most vocabularies cap numeric merges at three digits.
///   - punctuation   : 1 per character — JSON structure ({ } [ ] " : ,) rarely merges beyond pairs,
///                     and this is deliberately the pessimistic direction: structural overhead is the
///                     cost a response-shape change is most likely to add without anyone noticing.
///   - whitespace    : free — BPE folds a single leading space into the following word, and the wire
///                     format is compact anyway.
///
/// Treat absolute values as "the right order of magnitude"; treat differences between two measurements
/// taken the same way as real.
/// </summary>
public static class TokenEstimator
{
    private const int LetterCharsPerToken = 4;
    private const int DigitCharsPerToken = 3;

    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var tokens = 0;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                tokens += CeilDiv(i - start, LetterCharsPerToken);
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                tokens += CeilDiv(i - start, DigitCharsPerToken);
                continue;
            }

            // Punctuation and everything else: one token per character.
            tokens++;
            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Estimates the cost of a value as it would actually reach the model: serialized to the compact
    /// camelCase wire format, then put through <see cref="CampaignVault.Middleware.McpResponseCleaner"/>
    /// so vector fields, empty containers, and nulls are gone. Measuring the raw DTO instead would
    /// credit the response shape for bytes the transport already strips, and would miss regressions
    /// that only show up post-cleaning.
    /// </summary>
    public static (int Tokens, int Chars, string Json) EstimateWireCost<T>(T value, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(value, options);
        var cleaned = CampaignVault.Middleware.McpResponseCleaner.Clean(element);

        var json = cleaned is null
            ? JsonSerializer.Serialize(element, options)
            : cleaned.ToJsonString(CompactOptions);

        return (Estimate(json), json.Length, json);
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { WriteIndented = false };

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    /// <summary>
    /// Per-property token cost of an object, largest first — answers "what is actually expensive in
    /// this response?" when a budget test fails, instead of leaving someone to eyeball a 40KB blob.
    /// </summary>
    public static IEnumerable<(string Property, int Tokens)> Breakdown(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject obj)
        {
            yield break;
        }

        var costs = new List<(string Property, int Tokens)>();
        foreach (var (key, value) in obj)
        {
            costs.Add((key, Estimate(key) + Estimate(value?.ToJsonString(CompactOptions) ?? "null")));
        }

        foreach (var cost in costs.OrderByDescending(c => c.Tokens))
        {
            yield return cost;
        }
    }
}
