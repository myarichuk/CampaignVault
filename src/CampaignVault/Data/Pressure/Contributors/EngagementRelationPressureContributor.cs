using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class EngagementRelationPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Character:EngagementLock";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 50;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();

        if (ctx.Scene?.PresentNPCs != null)
        {
            foreach (var npc in ctx.Scene.PresentNPCs)
            {
                if (npc.SystemStats?.EngagementRelations == null) continue;

                foreach (var relation in npc.SystemStats.EngagementRelations.Where(EngagementRelationCatalog.EmitsPressure))
                {
                    var metadata = EngagementRelationCatalog.GetMetadata(relation);
                    var description = EngagementRelationCatalog.FormatDescription(npc.Name, relation);
                    pressures.Add(new WorldPressureItem(
                        PressureSeverity.NarrativePrompt,
                        npc.Id,
                        $"{description} {metadata.ResolutionPrompt}",
                        GroupingKey));
                }
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}