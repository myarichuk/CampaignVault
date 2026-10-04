using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data;

/// <summary>
/// Resolves combat effects with relative own-turn-start expiry (e.g. Acid Arrow's delayed tick):
/// rolls any <see cref="StatusEffect.PendingDamage"/>, applies it as damage, removes the effects, and reports
/// narrative lines plus domain events for the caller to publish. The damage goes through the same
/// <see cref="DamageApplication"/> as <see cref="HpChangeHandler"/> (MaxHp guard, mitigation, temporary hit points,
/// downed and dying rules, concentration saves); combat turn advancement has no WorldChange batch in scope, so the tick
/// can't flow through the handler itself and supplies its own sink.
/// </summary>
/// <remarks>
/// Mitigation reads the stat block only: the processor has no feat providers, so a feat's conditional resistance (Rage)
/// doesn't apply to a tick.
/// </remarks>
internal static class DelayedTickProcessor
{
    internal static async Task<IReadOnlyList<(string Topic, object? Data)>> ProcessAsync(
        Character character,
        IRollService? rollService,
        List<string> messages,
        Func<Character, Task>? onConcentrationBroken = null,
        Func<Character, Task>? onDowned = null,
        CancellationToken ct = default,
        Func<Task<int>>? currentDay = null)
    {
        var events = new List<(string Topic, object? Data)>();
        var effects = character.SystemStats?.StatusEffects;
        if (effects is null)
        {
            return events;
        }

        var sink = new TickSink(messages, events, onConcentrationBroken, onDowned, currentDay);
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
            var damage = character.SystemStats is { } stats
                ? DamageResolver.Resolve(
                    outcome.Result, pending.DamageType, stats, [], null,
                    stats is Pf2eExtension ? DamageOrdering.Pf2e : DamageOrdering.Dnd5e)
                : outcome.Result;
            var damageLabel = string.IsNullOrWhiteSpace(pending.DamageType) ? "damage" : $"{pending.DamageType} damage";
            effects.Remove(effect);

            // The effect's own line leads; whatever the damage then caused (dying, concentration) follows it.
            var at = messages.Count;
            var applied = await DamageApplication.ApplyAsync(
                character, damage, critical: false, attackerId: null, sink, rollService, announceHp: false, ct);
            messages.Insert(at, $"'{effect.Name}' on '{character.Name}' dealt {applied.HpLost} {damageLabel} ({outcome.Result} rolled).");
        }

        return events;
    }

    private sealed class TickSink(
        List<string> messages,
        List<(string Topic, object? Data)> events,
        Func<Character, Task>? onConcentrationBroken,
        Func<Character, Task>? onDowned,
        Func<Task<int>>? currentDay) : IDamageSink
    {
        public void Message(string text) => messages.Add(text);

        public void Publish(string topic, Dictionary<string, object?> data) => events.Add((topic, data));

        public async Task LapseMinionsAsync(Character caster, bool boundOnly, CancellationToken ct)
        {
            var callback = boundOnly ? onConcentrationBroken : onDowned;
            if (callback != null)
            {
                await callback(caster);
            }
        }

        public async Task DieAsync(Character character, string cause, string? killerId, CancellationToken ct)
        {
            if (character.IsDead)
            {
                return;
            }

            var day = currentDay != null ? await currentDay() : 0;
            DeathChangeHandler.ApplyDeathState(character, day, cause, killerId);
            if (onDowned != null)
            {
                await onDowned(character);
            }

            Message($"{character.Name} ({character.Id}) died: {cause}.");
            Publish(CoreEvents.CharacterDied, new Dictionary<string, object?>
            {
                [CoreEvents.Fields.CharacterId] = character.Id,
                [CoreEvents.Fields.Cause] = cause,
                [CoreEvents.Fields.KillerId] = killerId,
                [CoreEvents.Fields.BodyLocationId] = character.Death?.BodyLocationId,
                [CoreEvents.Fields.Day] = day,
            });
        }
    }
}
