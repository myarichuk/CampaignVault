using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles spending or recovering resource pools (spell slots, focus points, action points, etc.).
/// Validates pool existence, clamps values to [0, max], and emits narrative for contextual feedback.
/// </summary>
public class ResourceChangeHandler : IWorldChangeHandler
{
    private readonly SpellDefinitionProvider? _spellProvider;

    public ResourceChangeHandler(SpellDefinitionProvider? spellProvider = null)
    {
        _spellProvider = spellProvider;
    }

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

        var spellFailure = TryValidateSpellSpend(rc, character, context);
        if (spellFailure != null)
            return spellFailure.Value;

        // Clamp the new value to [0, max]
        var oldCurrent = pool.Current;
        var newCurrent = Math.Clamp(oldCurrent + rc.Delta, 0, pool.Max);
        var actualDelta = newCurrent - oldCurrent;

        // Update the pool
        var updatedPool = rc.RecoveredOnDay.HasValue
            ? pool with { Current = newCurrent, LastRecoveredDay = rc.RecoveredOnDay.Value }
            : pool with { Current = newCurrent };
        character.SystemStats.ResourcePools[rc.PoolName] = updatedPool;

        var narrative = rc.Reason ?? "Resource pool updated.";
        if (actualDelta != rc.Delta)
        {
            narrative += $" (Clamped: requested {rc.Delta:+0;-0}, actual {actualDelta:+0;-0})";
        }

        return new ChangeHandlerResult(true, $"{character.Name}'s {rc.PoolName}: {oldCurrent} → {newCurrent}. {narrative}");
    }

    private ChangeHandlerResult? TryValidateSpellSpend(ResourceChange rc, Character character, ChangeContext context)
    {
        if (_spellProvider == null || !SpellSlotValidator.IsSpellSlotSpend(rc.Delta, rc.PoolName))
            return null;

        if (string.IsNullOrWhiteSpace(rc.SpellName))
        {
            context.RecordMessage(
                "[WARNING] spell_slots spend without spellName — slot-level validation skipped. " +
                "Set spellName from get_spells so the engine can verify slot level.");
            return null;
        }

        if (!RulesetSystemResolver.TryFromStats(character.SystemStats, out var system))
        {
            context.RecordMessage(
                "[WARNING] Cannot resolve ruleset system for spell validation; spend was applied.");
            return null;
        }
        if (!_spellProvider.TryGet(system, rc.SpellName, out var spell) || spell == null)
        {
            context.RecordMessage(
                $"[WARNING] spellName '{rc.SpellName}' did not match any known {system} spell definition. " +
                $"Spend was applied; call get_spells to verify slot usage.");
            return null;
        }

        if (SpellSlotValidator.IsCantrip(spell))
        {
            context.RecordMessage(SpellSlotValidator.CantripWarning(spell));
            return null;
        }

        if (!SpellSlotValidator.TryParseSlotLevel(rc.PoolName, out var slotLevel))
            return null;

        var error = SpellSlotValidator.ValidateSpend(spell, slotLevel);
        if (error != null)
            return ChangeHandlerResult.Failure(error);

        var concentrationHint = SpellSlotValidator.BuildConcentrationHint(spell);
        if (concentrationHint != null)
            context.RecordMessage(concentrationHint);

        return null;
    }
}