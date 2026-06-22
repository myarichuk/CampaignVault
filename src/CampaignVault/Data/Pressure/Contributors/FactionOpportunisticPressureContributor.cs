using CampaignVault.Data.Pressure;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class FactionOpportunisticPressureContributor : IPressureContributor
{
    public static string GetOpportunisticGroupingKey(string factionId) => $"Faction:Opportunistic:{factionId}";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 55;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.RelevantFactions == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var contextBonus = SceneVulnerabilityHeuristics.ScoreSceneContext(ctx.Scene);
        var present = ctx.Scene.PresentNPCs?.ToList() ?? [];
        var alreadyCovered = present.Any(npc =>
        {
            var score = SceneVulnerabilityHeuristics.ScoreCharacter(npc, contextBonus);
            return score.Total >= SceneVulnerabilityHeuristics.SuggestionThreshold;
        });

        if (alreadyCovered)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var f in ctx.Scene.RelevantFactions.Where(f => f.LocalStance == FactionStance.Opportunistic))
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, f.FactionId,
                $"An Opportunistic faction ('{f.Name}') is present. Tag vulnerable appearance via character_update "
                + "(tagsToAdd: bloody, disheveled, wanted, unarmed, etc.) when narration establishes it — "
                + "the engine scores visualTags for crowd-reaction pressure. "
                + "If they look exploitable, narrate robbery/ambush or promote a single aggressor from ambientCrowd.",
                GetOpportunisticGroupingKey(f.FactionId)));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}