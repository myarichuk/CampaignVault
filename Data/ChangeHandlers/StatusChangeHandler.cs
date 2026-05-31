using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles both adding and removing character statuses.
/// 
/// Design decisions (as of June 2026):
/// - Status is intentionally a List (multiple different conditions can be active simultaneously, e.g. "Poisoned" + "Frightened").
/// - Add: appends the value as-is (duplicates of the exact same string are allowed to preserve previous loose behavior).
/// - Remove: removes *all* entries that match case-insensitively.
/// - Uses the pre-loaded Character from ChangeContext (eliminates the previous dangerous raw Patch pattern).
/// </summary>
public sealed class StatusChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change)
        => change is StatusChange or StatusRemove;

    public Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        switch (change)
        {
            case StatusChange add:
                return Task.FromResult(HandleAdd(add, context));

            case StatusRemove remove:
                return Task.FromResult(HandleRemove(remove, context));

            default:
                return Task.FromResult(ChangeHandlerResult.Failure("StatusChangeHandler received unexpected change type"));
        }
    }

    private ChangeHandlerResult HandleAdd(StatusChange add, ChangeContext context)
    {
        if (!context.Characters.TryGetValue(add.CharacterId, out var character) || character is null)
        {
            context.RecordMessage($"WARNING: Character {add.CharacterId} not found during StatusChange.");
            context.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        character.Status ??= new List<string>();
        character.Status.Add(add.Status);

        context.RecordMessage($"Status '{add.Status}' added to {add.CharacterId}");
        return ChangeHandlerResult.Ok;
    }

    private ChangeHandlerResult HandleRemove(StatusRemove remove, ChangeContext context)
    {
        if (!context.Characters.TryGetValue(remove.CharacterId, out var character) || character is null)
        {
            context.RecordMessage($"WARNING: Character {remove.CharacterId} not found during StatusRemove.");
            context.RecordFailure();
            return ChangeHandlerResult.Failure();
        }

        character.Status ??= new List<string>();

        var originalCount = character.Status.Count;
        var toRemove = remove.Status;

        // Case-insensitive removal of all matches
        character.Status.RemoveAll(s => string.Equals(s, toRemove, StringComparison.OrdinalIgnoreCase));

        var removedCount = originalCount - character.Status.Count;

        if (removedCount > 0)
        {
            context.RecordMessage($"Status '{remove.Status}' removed from {remove.CharacterId} ({removedCount} occurrence(s))");
        }
        else
        {
            context.RecordMessage($"StatusRemove: '{remove.Status}' was not present on {remove.CharacterId}");
            // Not a failure - removing a non-existent status is harmless (idempotent)
        }

        return ChangeHandlerResult.Ok;
    }
}