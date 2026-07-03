using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class TransientQuestGiverPressureContributor : IPressureContributor
{
    public const string GroupingKey = "Character:TransientQuestGiver";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 35;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene?.PresentNPCs == null || ctx.Scene.ActiveQuests == null)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        foreach (var npc in ctx.Scene.PresentNPCs)
        {
            if (!npc.KeepAlive && ctx.Scene.ActiveQuests.Any(q => q.GiverId == npc.Id))
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, npc.Id,
                    $"Character '{npc.Name}' is a Quest Giver but is marked as transient (KeepAlive = false). The engine will evict them from their current location when the party leaves (recoverable — they stay in the database and can reappear by reassigning their location; no data is deleted, but they'll be gone from the scene). Anchor them immediately:\n[ {{ \"$type\": \"character_update\", \"characterId\": \"{npc.Id}\", \"keepAlive\": true }} ]",
                    GroupingKey));
            }
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}