using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Resolves combat effects with relative own-turn-start expiry (e.g. Acid Arrow's delayed tick):
/// rolls any <see cref="StatusEffect.PendingDamage"/>, applies it as direct HP loss, removes the
/// effects, and reports narrative lines plus domain events for the caller to publish.
/// Damage semantics deliberately mirror <see cref="HpChangeHandler"/> (MaxHp guard, clamp,
/// CharacterDamaged/CharacterDowned events, concentration saves) — combat turn advancement has no
/// WorldChange batch in scope, so the tick can't flow through the handler itself.
/// </summary>
internal static class DelayedTickProcessor
{
    internal static async Task<IReadOnlyList<(string Topic, object? Data)>> ProcessAsync(
        Character character,
        IRollService? rollService,
        List<string> messages,
        Func<Character, Task>? onConcentrationBroken = null,
        Func<Character, Task>? onDowned = null,
        CancellationToken ct = default)
    {
        var events = new List<(string Topic, object? Data)>();
        var effects = character.SystemStats?.StatusEffects;
        if (effects is null)
        {
            return events;
        }

        var expiring = effects.Where(e => e.ExpiresAtOwnTurnStart).ToList();
        foreach (var effect in expiring)
        {
            var pending = effect.PendingDamage;
            if (pending is null || string.IsNullOrWhiteSpace(pending.DiceExpression))
            {
                effects.Remove(effect);
                messages.Add($"Expired effect '{effect.Name}' on '{character.Name}'.");
                continue;
            }

            if (rollService is null)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve pending damage on '{effect.Name}' ({character.Id}): no IRollService is available.");
            }

            if (character.MaxHp <= 0)
            {
                effects.Remove(effect);
                messages.Add($"WARNING: Pending damage on '{effect.Name}' skipped for {character.Id} — MaxHp is {character.MaxHp}.");
                continue;
            }

            var outcome = await rollService.RollAsync(
                new RollRequest { Tag = "delayed-tick", Expression = pending.DiceExpression, Mechanic = DiceMechanic.Standard }, ct);
            var hpBefore = character.CurrentHp;
            character.CurrentHp = Math.Clamp(character.CurrentHp - outcome.Result, 0, character.MaxHp);
            var hpLost = hpBefore - character.CurrentHp;
            var damageLabel = string.IsNullOrWhiteSpace(pending.DamageType) ? "damage" : $"{pending.DamageType} damage";
            messages.Add($"'{effect.Name}' on '{character.Name}' dealt {hpLost} {damageLabel} ({outcome.Result} rolled).");
            effects.Remove(effect);

            events.Add((CoreEvents.CharacterDamaged, new Dictionary<string, object?>
            {
                [CoreEvents.Fields.CharacterId] = character.Id,
                [CoreEvents.Fields.Amount] = outcome.Result,
                [CoreEvents.Fields.HpLost] = hpLost,
                [CoreEvents.Fields.CurrentHp] = character.CurrentHp,
                [CoreEvents.Fields.MaxHp] = character.MaxHp,
                [CoreEvents.Fields.ActorId] = null,
            }));

            if (hpBefore > 0 && character.CurrentHp == 0)
            {
                events.Add((CoreEvents.CharacterDowned, new Dictionary<string, object?>
                {
                    [CoreEvents.Fields.CharacterId] = character.Id,
                    [CoreEvents.Fields.MaxHp] = character.MaxHp,
                    [CoreEvents.Fields.ActorId] = null,
                }));
                if (onDowned != null)
                {
                    await onDowned(character);
                }
            }

            await CheckConcentrationAsync(character, outcome.Result, rollService, messages, onConcentrationBroken, ct);
        }

        return events;
    }

    private static async Task CheckConcentrationAsync(
        Character character,
        int damageTaken,
        IRollService rollService,
        List<string> messages,
        Func<Character, Task>? onConcentrationBroken,
        CancellationToken ct)
    {
        if (damageTaken <= 0 || character.SystemStats?.StatusEffects is null)
        {
            return;
        }

        var concentration = character.SystemStats.StatusEffects
            .FirstOrDefault(e => e.Name.Contains("Concentration", StringComparison.OrdinalIgnoreCase));
        if (concentration is null)
        {
            return;
        }

        var dc = Math.Max(10, (int)Math.Ceiling(damageTaken / 2.0f));
        var (saveMod, saveLabel) = HpChangeHandler.GetConcentrationSaveModifier(character.SystemStats);
        var outcome = await rollService.RollAsync(
            new RollRequest { Tag = "concentration", Expression = "1d20", Bonus = saveMod }, ct);

        if (outcome.Result < dc)
        {
            character.SystemStats.StatusEffects.Remove(concentration);
            messages.Add($"Concentration broken for {character.Id}: {damageTaken} damage (DC {dc}), {saveLabel} save {outcome.Result} failed.");
            if (onConcentrationBroken != null)
            {
                await onConcentrationBroken(character);
            }
        }
        else
        {
            messages.Add($"Concentration held for {character.Id}: {damageTaken} damage (DC {dc}), {saveLabel} save {outcome.Result} succeeded.");
        }
    }
}
