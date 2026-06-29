using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles spending or recovering resource pools (spell slots, focus points, action points, etc.).
/// Validates pool existence, clamps values to [0, max], and emits narrative for contextual feedback.
/// </summary>
public class ResourceChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ResourceChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var rc = (ResourceChange)change;

        if (string.IsNullOrWhiteSpace(rc.CharacterId))
        {
            return ChangeHandlerResult.Failure("CharacterId is required.");
        }

        if (string.IsNullOrWhiteSpace(rc.PoolName))
        {
            return ChangeHandlerResult.Failure("PoolName is required.");
        }

        if (!context.Characters.TryGetValue(rc.CharacterId, out var character))
        {
            character = context.Session != null
                ? await context.Session.LoadAsync<Character>(rc.CharacterId, ct)
                : null;

            if (character == null)
            {
                return ChangeHandlerResult.Failure($"Character '{rc.CharacterId}' not found.");
            }

            context.RegisterNewCharacter(character);
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, context.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        if (character.SystemStats?.ResourcePools == null || !character.SystemStats.ResourcePools.TryGetValue(rc.PoolName, out var pool))
        {
            return ChangeHandlerResult.Failure($"Resource pool '{rc.PoolName}' does not exist for character '{rc.CharacterId}'.");
        }

        // Clamp the new value to [0, max]
        var oldCurrent = pool.Current;
        var newCurrent = Math.Clamp(oldCurrent + rc.Delta, 0, pool.Max);
        var actualDelta = newCurrent - oldCurrent;

        // Update the pool
        var updatedPool = pool with { Current = newCurrent };
        character.SystemStats.ResourcePools[rc.PoolName] = updatedPool;

        var narrative = rc.Reason ?? "Resource pool updated.";
        if (actualDelta != rc.Delta)
        {
            narrative += $" (Clamped: requested {rc.Delta:+0;-0}, actual {actualDelta:+0;-0})";
        }

        return new ChangeHandlerResult(true, $"{character.Name}'s {rc.PoolName}: {oldCurrent} → {newCurrent}. {narrative}");
    }
}
