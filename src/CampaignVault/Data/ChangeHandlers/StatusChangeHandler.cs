using System.Linq;
using CampaignVault.Models;
using CampaignVault.Services;

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
    private readonly ConditionDefinitionProvider _conditionProvider;

    public StatusChangeHandler(ConditionDefinitionProvider conditionProvider)
    {
        _conditionProvider = conditionProvider ?? throw new ArgumentNullException(nameof(conditionProvider));
    }

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
            character = await context.Session.LoadAsync<Character>(add.CharacterId, ct);
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(add.CharacterId);
                var msg = $"Character {add.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

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
            RecordConditionValidationWarning(context, character.SystemStats, effect);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(add.Status))
            {
                return ChangeHandlerResult.Failure(
                    "Neither 'effect' nor 'status' was provided — a status change needs one of the two.");
            }

            // Legacy fallback: plain string Status → minimal StatusEffect
            effect = new StatusEffect
            {
                Name     = add.Status,
                Category = "Legacy",
                AppliedBy = "legacy-status-change"
            };
        }

        // A character can only concentrate on one effect at a time — casting a new
        // concentration effect breaks whatever it was previously concentrating on.
        if (effect.Name.Contains("Concentration", StringComparison.OrdinalIgnoreCase))
        {
            var broken = character.SystemStats.StatusEffects
                .Where(e => e.Name.Contains("Concentration", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var priorEffect in broken)
            {
                character.SystemStats.StatusEffects.Remove(priorEffect);
                context.RecordMessage($"Concentration on '{priorEffect.Name}' broken for {add.CharacterId} by casting '{effect.Name}'.");
            }
        }

        character.SystemStats.StatusEffects.Add(effect);
        context.RecordMessage($"Status '{effect.Name}' (category: {effect.Category}) added to {add.CharacterId}");

        await LogStatusEventAsync(context, character, $"{character.Name} gained status '{effect.Name}' ({effect.Category}).");

        return ChangeHandlerResult.Ok;
    }

    // Status effects (restrained, poisoned, etc.) often gate what actions are legal, so their history
    // is Important, not Trivial. Auto-logged so recall_history/NpcRecentEvents can surface it without a
    // second, separate `event` commit for the same narrative beat.
    private static async Task LogStatusEventAsync(ChangeContext context, Character character, string summary)
    {
        await context.LogEventAsync(new Event
        {
            Id = "events/" + Guid.NewGuid(),
            Summary = summary,
            Category = EventCategory.Interaction,
            Importance = MemoryImportance.Important,
            Involved = [character.Id],
            LocationId = character.CurrentLocationId,
            DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
            CampaignName = context.CampaignName,
        });
    }

    private void RecordConditionValidationWarning(
        ChangeContext context,
        SystemExtension stats,
        StatusEffect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.ConditionName))
            return;

        if (!RulesetSystemResolver.TryFromStats(stats, out var system))
        {
            context.RecordMessage(
                $"[WARNING] conditionName '{effect.ConditionName}' could not be validated — unknown system stats type. " +
                "Effect was applied with expiresAtDay/expiresAtRound heuristics only.");
            return;
        }

        if (_conditionProvider.TryGet(system, effect.ConditionName, out _))
            return;

        const int maxSample = 8;
        var knownNames = _conditionProvider.GetConditionsForSystem(system).Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sample = string.Join(", ", knownNames.Take(maxSample));
        var overflow = knownNames.Count > maxSample
            ? $" (+{knownNames.Count - maxSample} more via get_system_handbook)"
            : string.Empty;

        context.RecordMessage(
            $"[WARNING] conditionName '{effect.ConditionName}' did not match any known {system} condition definition. " +
            $"Sample conditions: {sample}{overflow}. " +
            "Effect was applied, but expiry will fall back to expiresAtDay/expiresAtRound heuristics.");
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
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

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
        {
            context.RecordMessage($"Status '{remove.Status}' removed from {remove.CharacterId} ({removedCount} effect(s))");
            await LogStatusEventAsync(context, character, $"{character.Name} lost status '{remove.Status}'.");
        }
        else
        {
            context.RecordMessage($"StatusRemove: '{remove.Status}' was not present on {remove.CharacterId} (no-op)");
        }

        // Removing a non-existent status is harmless (idempotent)
        return ChangeHandlerResult.Ok;
    }
}