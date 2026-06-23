using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class IncompleteSystemStatsPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Character:IncompleteSystemStats";

    public PressureScope Scope => PressureScope.World;
    public int Order => 15;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var activeSystem = ctx.Config.ActiveSystem;
        var characters = await PressureQueryHelper.QueryCombatantCharactersAsync(ctx.Session, ctx.CampaignName, 100, ct);
        var pressures = new List<WorldPressureItem>();

        foreach (var character in characters)
        {
            if (SystemStatsCompleteness.IsComplete(character, activeSystem))
            {
                continue;
            }

            var missing = SystemStatsCompleteness.GetMissingFields(character, activeSystem);
            var missingSummary = missing.Count > 0
                ? string.Join(", ", missing)
                : "ruleset combat stats";

            pressures.Add(new WorldPressureItem(
                PressureSeverity.EngineWarning,
                character.Id,
                $"[ENGINE] {character.Name} has uninitialized systemStats ({missingSummary}). "
                + $"Combatants (KeepAlive or maxHp > 0) MUST have ruleset stats bootstrapped. "
                + $"PCs: omit maxHp — commit bootstrap fields (hitDie, level, constitution, etc.) and the engine derives HP/AC. "
                + $"Creature stat blocks: use systemStats.statBlockHp or maxHp. Example PC bootstrap for {activeSystem}: "
                + SystemStatsCompleteness.BuildExampleCommit(character, activeSystem)
                + " Stat-block example: "
                + SystemStatsCompleteness.BuildStatBlockExampleCommit(character, activeSystem),
                GroupingKey));
        }

        return pressures;
    }
}