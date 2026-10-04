using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Contributors;

public sealed class Dnd5eExhaustionPressureContributor : IRulesetPressureContributor
{
    public const string GroupingKey = "Character:Attribute:exhaustion_level";

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
            // Mechanical D&D 5e exhaustion (1-6 scale), distinct from the ruleset-agnostic
            // narrative "tiredness" need read by CharacterDistressPressureContributor.
            var exhaustion = Level(c);
            if (exhaustion >= 3)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, c.Id,
                    $"{c.Name} has exhaustion level {exhaustion} (D&D 5e, 1-6 scale). The engine applies disadvantage on checks, attacks and saves, and reduced speed. Narrate fatigue and consider rest.",
                    GroupingKey));
            }
        }

        return pressures;
    }

    /// <summary>
    /// The character's exhaustion level: the "Exhaustion N" status is canonical; the old <c>exhaustion_level</c> attribute is
    /// still read as a fallback, and the higher of the two wins.
    /// </summary>
    public static int Level(Character character)
    {
        var level = 0;
        foreach (var status in character.SystemStats?.StatusEffects ?? [])
        {
            var named = string.Equals(status.ConditionName, "exhaustion", StringComparison.OrdinalIgnoreCase)
                || status.Name.StartsWith("exhaustion", StringComparison.OrdinalIgnoreCase);
            if (named)
                level = Math.Max(level, ConditionExpiryEvaluator.TryParseStackLevel(status.Name, out var parsed) ? parsed : 1);
        }

        if (character.SystemStats?.Attributes != null
            && character.SystemStats.Attributes.TryGetValue("exhaustion_level", out var attribute))
        {
            level = Math.Max(level, (int)attribute);
        }

        return level;
    }
}
