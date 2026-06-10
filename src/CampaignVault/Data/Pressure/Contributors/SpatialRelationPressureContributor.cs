using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class SpatialRelationPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Character:SpatialLock";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 50;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (ctx.Scene?.PresentNPCs != null)
        {
            foreach (var npc in ctx.Scene.PresentNPCs)
            {
                if (npc.SystemStats?.SpatialRelations != null)
                {
                    var locks = npc.SystemStats.SpatialRelations
                        .Where(r => r.RelationType == "GrappledBy" || r.RelationType == "Grappling" || r.RelationType == "LeaningIn")
                        .ToList();

                    foreach (var l in locks)
                    {
                        pressures.Add(new WorldPressureItem(
                            PressureSeverity.NarrativePrompt,
                            npc.Id,
                            $"Character '{npc.Name}' has a restricting spatial relationship '{l.RelationType}' with '{l.TargetId}'. Narrate how they attempt to escape or resolve the situation in your next action.",
                            GroupingKey));
                    }
                }
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}
