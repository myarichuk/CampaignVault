using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Pressure;

public sealed class PressureOrchestrator : IPressureOrchestrator
{
    private readonly IEnumerable<IPressureContributor> _contributors;
    private readonly IPressureManager _pressureManager;
    private readonly IRulesetModuleSelector _rulesetSelector;

    public PressureOrchestrator(
        IEnumerable<IPressureContributor>? contributors,
        IPressureManager pressureManager,
        IRulesetModuleSelector rulesetSelector)
    {
        _contributors = contributors ?? [];
        _pressureManager = pressureManager ?? throw new ArgumentNullException(nameof(pressureManager));
        _rulesetSelector = rulesetSelector ?? throw new ArgumentNullException(nameof(rulesetSelector));
    }

    public async Task<List<WorldPressureItem>> CollectAndCapAsync(PressureScope scope, PressureContext ctx, CancellationToken ct = default)
    {
        var merged = new Dictionary<string, WorldPressureItem>(StringComparer.Ordinal);

        async Task CollectFromAsync(IEnumerable<IPressureContributor> contributors)
        {
            foreach (var contributor in contributors
                         .Where(c => (c.Scope & scope) != 0)
                         .OrderBy(c => c.Order))
            {
                var items = await contributor.EvaluateAsync(ctx, ct);
                foreach (var item in items)
                {
                    var key = $"{item.GroupingKey}:{item.EntityId}";
                    merged[key] = item;
                }
            }
        }

        await CollectFromAsync(_contributors);

        var module = _rulesetSelector.GetModule(ctx.Config.ActiveSystem);
        await CollectFromAsync(module.PressureContributors);

        return await _pressureManager.FilterAndCapAsync(
            ctx.Session,
            ctx.CampaignName,
            (int)ctx.Time.TotalDaysElapsed,
            merged.Values,
            ctx.DisableCooldowns);
    }
}