using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles both adding and removing character status effects.
///
/// Design decisions (Phase 1, June 2026):
/// - StatusEffect is an LLM-authored structured object (name, category, affectedPart,
///   statModifiers, expiration, recoveryHint). Replaces the old flat List&lt;string&gt;.
/// - Add (StatusChange.StatusEffect): appends a new StatusEffect to SystemStats.StatusEffects.
///   Duplicates (same Name) are allowed — the LLM may stack identical condition names (e.g. two
///   separate Frightened sources). De-duplication is the LLM DM's responsibility.
/// - Remove (StatusRemove.Status): removes ALL StatusEffects whose Name matches case-insensitively.
/// - Legacy path: StatusChange.Status (plain string) is still accepted for backward compatibility
///   and creates a minimal StatusEffect with just a Name and Category="Legacy".
/// - Auto-expiry (ExpiresAtDay / ExpiresAtRound) is enforced by AdvanceWorldAsync / CombatEncounter
///   advancement, not here.
/// </summary>
public sealed class StatusChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change)
        => change is StatusChange or StatusRemove;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        return change switch
        {
            StatusChange add    => await HandleAdd(add, context, ct),
            StatusRemove remove => await HandleRemove(remove, context, ct),
            _                  => ChangeHandlerResult.Failure("StatusChangeHandler received unexpected change type")
        };
    }

    // ── Add ───────────────────────────────────────────────────────────────────

    private async Task<ChangeHandlerResult> HandleAdd(StatusChange add, ChangeContext context, CancellationToken ct)
    {
        if (!context.Characters.TryGetValue(add.CharacterId, out var character))
        {
            character = context.Session != null ? await context.Session.LoadAsync<Character>(add.CharacterId, ct) : null;
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(add.CharacterId);
                var msg = $"Character {add.CharacterId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        character.SystemStats ??= new SystemExtension();
        character.SystemStats.StatusEffects ??= [];

        StatusEffect effect;

        if (add.Effect is not null)
        {
            // Preferred path: fully structured StatusEffect from LLM DM
            effect = add.Effect;
        }
        else
        {
            // Legacy fallback: plain string Status → minimal StatusEffect
            effect = new StatusEffect
            {
                Name     = add.Status,
                Category = "Legacy",
                AppliedBy = "legacy-status-change"
            };
        }

        character.SystemStats.StatusEffects.Add(effect);
        context.RecordMessage($"Status '{effect.Name}' (category: {effect.Category}) added to {add.CharacterId}");
        return ChangeHandlerResult.Ok;
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    private async Task<ChangeHandlerResult> HandleRemove(StatusRemove remove, ChangeContext context, CancellationToken ct)
    {
        if (!context.Characters.TryGetValue(remove.CharacterId, out var character))
        {
            character = context.Session != null ? await context.Session.LoadAsync<Character>(remove.CharacterId, ct) : null;
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(remove.CharacterId);
                var msg = $"Character {remove.CharacterId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        character.SystemStats ??= new SystemExtension();
        character.SystemStats.StatusEffects ??= [];

        var toRemove = remove.Status;
        var originalCount = character.SystemStats.StatusEffects.Count;

        // Case-insensitive removal of all matching status effects
        character.SystemStats.StatusEffects.RemoveAll(e =>
            string.Equals(e.Name, toRemove, StringComparison.OrdinalIgnoreCase));

        var removedCount = originalCount - character.SystemStats.StatusEffects.Count;

        if (removedCount > 0)
            context.RecordMessage($"Status '{remove.Status}' removed from {remove.CharacterId} ({removedCount} effect(s))");
        else
            context.RecordMessage($"StatusRemove: '{remove.Status}' was not present on {remove.CharacterId} (no-op)");

        // Removing a non-existent status is harmless (idempotent)
        return ChangeHandlerResult.Ok;
    }
}