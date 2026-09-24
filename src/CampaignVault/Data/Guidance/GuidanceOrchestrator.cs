using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data.Guidance;

internal sealed class GuidanceOrchestrator : IGuidanceOrchestrator
{
    /// <summary>
    /// Plugin hints admitted per response, so a chatty plugin can never crowd out core guidance
    /// (MaxGuidanceHintsPerResponse defaults to 2).
    /// </summary>
    internal const int MaxPluginHintsPerResponse = 1;

    private readonly IEnumerable<IGuidanceContributor> _contributors;
    private readonly IEnumerable<IPluginGuidanceContributor> _pluginContributors;
    private readonly CampaignDocumentKeys _keys;

    public GuidanceOrchestrator(
        IEnumerable<IGuidanceContributor> contributors,
        IEnumerable<IPluginGuidanceContributor> pluginContributors,
        CampaignDocumentKeys keys)
    {
        _contributors = contributors;
        _pluginContributors = pluginContributors;
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
            .Where(c => (c.Scope & scope) != 0)
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

        var pluginContributed = await CollectPluginHintsAsync(ctx, ct);

        // Filter by ledger: skip already-delivered unless ignoreLedger
        bool Deliverable(GuidanceHint h) =>
            ignoreLedger ||
            ledger?.Delivered.TryGetValue(h.Key, out var delivery) != true ||
            (h.RepeatAfterDays.HasValue && ctx.Time != null && delivery != null &&
             ctx.Time.TotalDaysElapsed - delivery.Day >= h.RepeatAfterDays.Value);

        // Cap plugin hints before the budget pass, or two high-priority plugin hints could take both slots.
        var admittedPluginHints = pluginContributed
            .Where(Deliverable)
            .OrderByDescending(h => h.Priority)
            .Take(MaxPluginHintsPerResponse);

        var filtered = contributed
            .Where(Deliverable)
            .Concat(admittedPluginHints)
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

    private async Task<List<GuidanceHint>> CollectPluginHintsAsync(PressureContext ctx, CancellationToken ct)
    {
        var hints = new List<GuidanceHint>();
        var pluginCtx = new PluginGuidanceContext(ctx);

        foreach (var contributor in _pluginContributors)
        {
            var source = contributor.GetType().Assembly.GetName().Name ?? contributor.GetType().Name;
            try
            {
                var result = await contributor.EvaluateAsync(pluginCtx, ct);
                hints.AddRange(result
                    .Where(h => !string.IsNullOrWhiteSpace(h.Key) && !string.IsNullOrWhiteSpace(h.Text))
                    .Select(h => new GuidanceHint($"plugin:{source}:{h.Key}", h.Text, GuidanceTrigger.Plugin, h.Priority)
                    {
                        Example = h.Example,
                        RepeatAfterDays = h.RepeatAfterDays,
                        Source = source
                    }));
            }
            catch
            {
                // Same policy as core contributors: a failing plugin must not break the tool response.
            }
        }

        return hints;
    }

    /// <summary>Raven-free adapter over <see cref="PressureContext"/> for plugin contributors.</summary>
    private sealed class PluginGuidanceContext(PressureContext ctx) : IGuidanceContext
    {
        public string CampaignName => ctx.CampaignName;
        public CampaignTime? Time => ctx.Time;
        public CampaignConfig? Config => ctx.Config;
        public IReadOnlyList<string> PartyCharacterIds => ctx.PartyCharacterIds ?? [];
        public IReadOnlyList<WorldChange> AppliedChanges => ctx.AppliedChanges ?? [];
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
