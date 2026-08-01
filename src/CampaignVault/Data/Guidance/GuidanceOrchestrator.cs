using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data.Guidance;

internal sealed class GuidanceOrchestrator : IGuidanceOrchestrator
{
    private readonly IEnumerable<IGuidanceContributor> _contributors;
    private readonly CampaignDocumentKeys _keys;

    public GuidanceOrchestrator(
        IEnumerable<IGuidanceContributor> contributors,
        CampaignDocumentKeys keys)
    {
        _contributors = contributors;
        _keys = keys;
    }

    public async Task<IReadOnlyList<GuidanceHint>> CollectAsync(
        PressureScope scope,
        PressureContext ctx,
        bool ignoreLedger = false,
        CancellationToken ct = default)
    {
        var hints = new List<GuidanceHint>();
        GuidanceLedger? ledger = null;

        if (ctx.Session != null && !ignoreLedger)
        {
            ledger = await ctx.Session.LoadAsync<GuidanceLedger>(
                _keys.StateGuidance(ctx.CampaignName));
        }

        var contributed = new List<GuidanceHint>();

        foreach (var contributor in _contributors
            .Where(c => c.Scope == scope)
            .OrderBy(c => c.Order))
        {
            try
            {
                var result = await contributor.EvaluateAsync(ctx, ct);
                contributed.AddRange(result);
            }
            catch
            {
                // Silently skip failed contributors to prevent guidance collection from breaking tool responses
            }
        }

        // Filter by ledger: skip already-delivered unless ignoreLedger
        var filtered = contributed
            .Where(h => ignoreLedger ||
                ledger?.Delivered.TryGetValue(h.Key, out var delivery) != true ||
                (h.RepeatAfterDays.HasValue && ctx.Time != null &&
                 ctx.Time.TotalDaysElapsed - delivery.Day >= h.RepeatAfterDays.Value))
            .OrderByDescending(h => h.Priority)
            .ToList();

        // Apply budget: accumulate text + example lengths, stop when over budget
        var charBudget = ctx.Config?.MaxGuidanceCharsPerResponse ?? 600;
        var hintBudget = ctx.Config?.MaxGuidanceHintsPerResponse ?? 2;
        var totalChars = 0;

        foreach (var hint in filtered.Take(hintBudget))
        {
            var hintSize = hint.Text.Length + (hint.Example?.Length ?? 0);
            if (totalChars + hintSize > charBudget)
            {
                // Truncate last hint at sentence boundary if it fits at all
                if (totalChars == 0 && hintSize > charBudget)
                {
                    var truncated = TruncateAtSentence(hint.Text, charBudget);
                    hints.Add(hint with { Text = truncated });
                }
                else if (totalChars < charBudget)
                {
                    hints.Add(hint);
                }
                break;
            }

            hints.Add(hint);
            totalChars += hintSize;
        }

        return hints.AsReadOnly();
    }

    private static string TruncateAtSentence(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;

        var truncated = text.Substring(0, maxLength);
        var lastPeriod = truncated.LastIndexOf('.');
        var lastQuestion = truncated.LastIndexOf('?');
        var lastExclamation = truncated.LastIndexOf('!');

        var lastSentenceEnd = Math.Max(
            lastPeriod,
            Math.Max(lastQuestion, lastExclamation));

        return lastSentenceEnd > maxLength * 0.7
            ? truncated.Substring(0, lastSentenceEnd + 1)
            : truncated + "…";
    }
}
