using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class PressureHintEnricher : IPressureContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 1000;

    public async Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var threshold = ctx.Config.CharacterPressureHpCriticalThreshold;
        var characters = await PressureQueryHelper.QueryKeepAliveCharactersAsync(ctx.Session, ctx.CampaignName, 100, ct);

        var pressures = new List<WorldPressureItem>();

        foreach (var c in characters)
        {
            if (c.CurrentHp <= c.MaxHp * threshold && c.CurrentHp > 0)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, c.Id,
                    $"{c.Name} is critically wounded ({c.CurrentHp}/{c.MaxHp} HP). Example fix in commit: [ {{\"$type\": \"hp\", \"characterId\": \"chars/xxx\", \"delta\": 10}}, {{\"$type\": \"status\", \"characterId\": \"chars/xxx\", \"status\": \"Stable\"}} ]",
                    CharacterDistressPressureContributor.CriticallyWoundedGroupingKey));
            }
            else if (c.CurrentHp <= 0)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, c.Id,
                    $"{c.Name} is dying or dead. Example fix in commit: [ {{\"$type\": \"hp\", \"characterId\": \"chars/xxx\", \"delta\": 10}}, {{\"$type\": \"status\", \"characterId\": \"chars/xxx\", \"status\": \"Stable\"}} ]",
                    CharacterDistressPressureContributor.DyingGroupingKey));
            }

            if (c.Needs?.ActiveNeeds != null)
            {
                foreach (var kvp in c.Needs.ActiveNeeds.Where(k => k.Value > 80f))
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, c.Id,
                        $"{c.Name} is in desperate need: {kvp.Key} ({kvp.Value:F0}%). Satisfy via: [ {{\"$type\": \"need\", \"characterId\": \"chars/xxx\", \"need\": \"hunger\", \"delta\": -30}} ] (negative = satisfy). Consider schedule_change if this NPC is important.",
                        CharacterDistressPressureContributor.GetNeedGroupingKey(kvp.Key)));
                }
            }
        }

        return pressures;
    }
}