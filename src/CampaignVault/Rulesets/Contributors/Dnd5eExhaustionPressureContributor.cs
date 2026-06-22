using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Contributors;

public sealed class Dnd5eExhaustionPressureContributor : IRulesetPressureContributor
{
    public const string GroupingKey = "Character:Attribute:exhaustion";

    public PressureScope Scope => PressureScope.World;
    public int Order => 25;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        var characters = await ctx.Session.Query<Character>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
            .Where(c => string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == ctx.CampaignName)
            .Where(c => c.KeepAlive)
            .Take(100)
            .ToListAsync();

        foreach (var c in characters)
        {
            if (c.SystemStats?.Attributes != null
                && c.SystemStats.Attributes.TryGetValue("exhaustion", out var exhaustion)
                && exhaustion >= 3f)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, c.Id,
                    $"{c.Name} has exhaustion level {exhaustion:F0} (D&D 5e). At level 3+, they suffer disadvantage on attacks/saves and reduced speed. Narrate fatigue and consider rest.",
                    GroupingKey));
            }
        }

        return pressures;
    }
}