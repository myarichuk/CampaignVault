using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data;

/// <summary>
/// Where <see cref="DamageApplication"/> reports and reaches out: messages for the DM, domain events, and the two
/// consequences that need a session (a death record, a caster's minions lapsing). The change handler backs it with its
/// context; combat turn advancement, which has no change batch, backs it with what it has.
/// </summary>
internal interface IDamageSink
{
    void Message(string text);

    void Publish(string topic, Dictionary<string, object?> data);

    /// <summary>The caster's bound minions end (<paramref name="boundOnly"/>: it lost concentration) or all of them (it dropped).</summary>
    Task LapseMinionsAsync(Character caster, bool boundOnly, CancellationToken ct);

    /// <summary>
    /// What the roll pipeline makes of a roll (conditions, statuses, plugin providers). A sink without a pipeline (turn
    /// advancement) leaves the bonus as it was.
    /// </summary>
    Task<RollResolution> ResolveRollAsync(RollQuery query, int baseBonus, CancellationToken ct) =>
        Task.FromResult(RollResolution.Unchanged(baseBonus));

    /// <summary>The character dies from this damage: record it, lapse what they held, publish the death.</summary>
    Task DieAsync(Character character, string cause, string? killerId, CancellationToken ct);
}

/// <summary>What one damage instance did.</summary>
/// <param name="HpLost">Hit points actually lost.</param>
/// <param name="TempAbsorbed">Damage the temporary hit points took first.</param>
/// <param name="Killed">The damage killed the character (massive damage, third death-save failure).</param>
internal sealed record DamageOutcome(int HpLost, int TempAbsorbed, bool Killed);

/// <summary>
/// The one place damage lands on a character: temporary hit points first, then hit points, then the downed and dying rules
/// and the concentration check. One call is one damage instance, so a hit is one death-save failure (two when
/// <c>critical</c>) and one concentration check however many dice it had.
/// </summary>
internal static class DamageApplication
{
    public static async Task<DamageOutcome> ApplyAsync(
        Character character,
        int amount,
        bool critical,
        string? attackerId,
        IDamageSink sink,
        IRollService rollService,
        bool announceHp,
        CancellationToken ct)
    {
        var hpBefore = character.CurrentHp;
        var absorbed = SpendTempHp(character, amount);
        var toHp = amount - absorbed;
        character.CurrentHp = Math.Clamp(hpBefore - toHp, 0, character.MaxHp);

        if (announceHp)
        {
            sink.Message(
                $"HP adjusted for {character.Id} by {-amount} (now {character.CurrentHp}/{character.MaxHp})"
                + (absorbed > 0 ? $"; {absorbed} absorbed by temporary hit points ({character.TempHp ?? 0} left)" : ""));
        }

        if (amount <= 0)
        {
            return new DamageOutcome(0, 0, false);
        }

        // Published on requested damage, not actual loss: a body already at 0 HP can still be hit (death saves in 5e, an
        // astral projector's abandoned body, ...).
        sink.Publish(CoreEvents.CharacterDamaged, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.CharacterId] = character.Id,
            [CoreEvents.Fields.Amount] = amount,
            [CoreEvents.Fields.HpLost] = hpBefore - character.CurrentHp,
            [CoreEvents.Fields.CurrentHp] = character.CurrentHp,
            [CoreEvents.Fields.MaxHp] = character.MaxHp,
            [CoreEvents.Fields.ActorId] = attackerId,
        });

        if (hpBefore > 0 && character.CurrentHp == 0)
        {
            sink.Publish(CoreEvents.CharacterDowned, new Dictionary<string, object?>
            {
                [CoreEvents.Fields.CharacterId] = character.Id,
                [CoreEvents.Fields.MaxHp] = character.MaxHp,
                [CoreEvents.Fields.ActorId] = attackerId,
            });
            await sink.LapseMinionsAsync(character, boundOnly: false, ct).ConfigureAwait(false);
        }

        var killed = await ApplyDyingRulesAsync(character, toHp, hpBefore, critical, attackerId, sink, ct).ConfigureAwait(false);

        // A character at 0 HP is unconscious from this moment, so the concentration check below sees them incapacitated.
        if (!killed)
        {
            HitPointState.SyncDowned(character);
        }

        // Concentration counts the damage taken, temporary hit points or not.
        if (!killed)
        {
            await CheckConcentrationAsync(character, amount, sink, rollService, ct).ConfigureAwait(false);
        }

        return new DamageOutcome(hpBefore - character.CurrentHp, absorbed, killed);
    }

    /// <summary>Takes up to <paramref name="amount"/> from the temporary hit points; returns what they absorbed.</summary>
    private static int SpendTempHp(Character character, int amount)
    {
        if (amount <= 0 || character.TempHp is not > 0)
        {
            return 0;
        }

        var absorbed = Math.Min(character.TempHp.Value, amount);
        var left = character.TempHp.Value - absorbed;
        character.TempHp = left > 0 ? left : null;
        if (left <= 0)
        {
            character.TempHpSource = null;
        }

        return absorbed;
    }

    /// <summary>
    /// 5e dying rules for player characters only (NPC death stays an explicit <c>death</c> commit): massive damage kills
    /// outright, damage at 0 HP adds a death-save failure (two for a critical hit), reaching 0 starts the tally. Only the
    /// damage that reached hit points counts, so temporary hit points that took the whole hit change nothing.
    /// </summary>
    private static async Task<bool> ApplyDyingRulesAsync(
        Character character, int toHp, int hpBefore, bool critical, string? attackerId, IDamageSink sink, CancellationToken ct)
    {
        if (Pf2eDying.Applies(character) && toHp > 0)
        {
            return await ApplyPf2eDyingRulesAsync(character, toHp, hpBefore, critical, attackerId, sink, ct).ConfigureAwait(false);
        }

        if (!HitPointState.UsesDeathSaves(character) || toHp <= 0)
        {
            return false;
        }

        var overflow = toHp - hpBefore;
        if (overflow >= character.MaxHp)
        {
            await sink.DieAsync(character, "massive damage", attackerId, ct).ConfigureAwait(false);
            return true;
        }

        if (hpBefore > 0)
        {
            if (character.CurrentHp == 0)
            {
                character.DeathSaves = new DeathSaveTally();
                sink.Message($"{character.Name} is down at 0 HP and dying: roll death saves with {{\"$type\":\"death_save\",\"characterId\":\"{character.Id}\"}} each turn.");
            }

            return false;
        }

        // Already at 0 HP: each hit is a failed death save (a stable body becomes unstable again), a critical hit two.
        var tally = character.DeathSaves ??= new DeathSaveTally();
        if (tally.Stable)
        {
            // A stable creature that takes damage starts a fresh set of death saves (SRD "Stabilizing a Creature").
            tally.Stable = false;
            tally.Successes = 0;
            tally.Failures = 0;
        }
        tally.Failures += critical ? 2 : 1;
        sink.Message(
            $"{character.Name} takes {(critical ? "a critical hit" : "damage")} at 0 HP: "
            + $"{(critical ? "two death save failures" : "death save failure")} ({Math.Min(tally.Failures, 3)}/3).");
        if (tally.Failures < 3)
        {
            return false;
        }

        await sink.DieAsync(character, "failed death saves", attackerId, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// PF2e dying rules for player characters (Player Core, "Dying"): damage that leaves a character at 0 HP gives dying 1
    /// (2 from a critical hit) plus their wounded value; damage while already dying adds 1 (2 from a critical hit) and no
    /// more wounded; dying 4, less their doomed value, is death. A recovery check each turn is rolled with <c>death_save</c>.
    /// </summary>
    private static async Task<bool> ApplyPf2eDyingRulesAsync(
        Character character, int toHp, int hpBefore, bool critical, string? attackerId, IDamageSink sink, CancellationToken ct)
    {
        // No instant-death rule here: Player Core's threshold could not be checked, so none is guessed (see the plan, Phase 6).
        var dying = Pf2eDying.Get(character, Pf2eDying.Dying);
        if (character.CurrentHp > 0)
        {
            return false;
        }

        string text;
        if (hpBefore > 0 || dying == 0)
        {
            // Knocked out: dying 1 (2 from a critical hit) plus the wounded value. At 0 HP and not dying (stable after a recovery
            // check) a fresh hit knocks them out again.
            var wounded = Pf2eDying.Get(character, Pf2eDying.Wounded);
            dying = Pf2eDying.ValueWhenDowned(character, critical);
            text = $"{character.Name} is knocked out: dying {dying}" + (wounded > 0 ? $" (wounded {wounded} adds to it)" : "") + ".";
        }
        else
        {
            dying += critical ? 2 : 1;
            text = $"{character.Name} takes {(critical ? "a critical hit" : "damage")} while dying: dying {dying}.";
        }

        if (Pf2eDying.IsDeadlyValue(character, dying))
        {
            sink.Message(text + $" That is dying {Pf2eDying.DeathThreshold(character)} or more.");
            await sink.DieAsync(character, "dying", attackerId, ct).ConfigureAwait(false);
            return true;
        }

        Pf2eDying.Set(character, Pf2eDying.Dying, dying);
        sink.Message(text + $" Roll a recovery check at the start of each of their turns with {{\"$type\":\"death_save\",\"characterId\":\"{character.Id}\"}}.");
        return false;
    }

    /// <summary>
    /// Concentration check: dropping to 0 HP or being incapacitated ends it outright, with no roll. Otherwise DC = max(10, half
    /// the damage), a save (CON for 5e, Fortitude for PF2e) rolled through the pipeline as <see cref="RollKinds.ConcentrationSave"/>
    /// so a plugin can grant advantage and conditions count.
    /// </summary>
    private static async Task CheckConcentrationAsync(
        Character character, int damageTaken, IDamageSink sink, IRollService rollService, CancellationToken ct)
    {
        if (character.SystemStats?.StatusEffects is null)
        {
            return;
        }

        var concentration = ConcentrationRules.Find(character.SystemStats.StatusEffects);
        if (concentration is null)
        {
            return;
        }

        if (character.CurrentHp <= 0 || ActionBlock.IsBlocked(character, out _))
        {
            character.SystemStats.StatusEffects.Remove(concentration);
            sink.Message($"Concentration broken for {character.Id}: {(character.CurrentHp <= 0 ? "down at 0 HP" : "incapacitated")}, no save.");
            await sink.LapseMinionsAsync(character, boundOnly: true, ct).ConfigureAwait(false);
            return;
        }

        // Only 5e breaks concentration on a damage save. PF2e sustains a spell as an action, and damage does not end it.
        if (character.SystemStats is Pf2eExtension)
        {
            return;
        }

        var dc = Math.Max(10, (int)Math.Ceiling(damageTaken / 2.0f));
        var (saveMod, saveLabel) = HpChangeHandler.GetConcentrationSaveModifier(character.SystemStats);
        var query = new RollQuery(
            RollKinds.ConcentrationSave, saveLabel == "CON" ? "constitution" : saveLabel.ToLowerInvariant(), [],
            character, null, SystemOf(character), new Dictionary<string, string>());
        var resolution = await sink.ResolveRollAsync(query, saveMod, ct).ConfigureAwait(false);
        var outcome = await rollService.RollAsync(
            new RollRequest
            {
                Tag = "concentration",
                Expression = "1d20",
                Bonus = resolution.Bonus,
                Mechanic = resolution.Advantage switch
                {
                    AdvantageEffect.Advantage => DiceMechanic.Advantage,
                    AdvantageEffect.Disadvantage => DiceMechanic.Disadvantage,
                    _ => DiceMechanic.Standard,
                },
            }, ct).ConfigureAwait(false);

        var notes = resolution.Notes.Count > 0 ? $" ({string.Join("; ", resolution.Notes)})" : "";
        if (resolution.AutoFail || outcome.Result < dc)
        {
            character.SystemStats.StatusEffects.Remove(concentration);
            sink.Message($"Concentration broken for {character.Id}: {damageTaken} damage (DC {dc}), {saveLabel} save {outcome.Result} failed{notes}.");
            await sink.LapseMinionsAsync(character, boundOnly: true, ct).ConfigureAwait(false);
        }
        else
        {
            sink.Message($"Concentration held for {character.Id}: {damageTaken} damage (DC {dc}), {saveLabel} save {outcome.Result} succeeded{notes}.");
        }
    }

    internal static string SystemOf(Character character) =>
        character.SystemStats is not null && RulesetSystemResolver.TryFromStats(character.SystemStats, out var system) ? system! : "";
}
