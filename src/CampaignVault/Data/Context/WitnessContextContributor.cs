using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data.Context;

/// <summary>What a beat gives off to anyone nearby.</summary>
[Flags]
public enum Perceptible
{
    None = 0,
    Loud = 1,
    Visible = 2,
    Both = Loud | Visible,
}

/// <summary>How perceptible a cast or an event is (WITNESS_CHANNEL_PLAN.md W2).</summary>
public static class Perceptibility
{
    public static Perceptible Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "loud" or "audible" or "sound" => Perceptible.Loud,
        "visible" or "visual" or "image" => Perceptible.Visible,
        "both" => Perceptible.Both,
        _ => Perceptible.None,
    };

    public static string Describe(Perceptible p) => p switch
    {
        Perceptible.Both => "loud+visible",
        Perceptible.Loud => "loud",
        Perceptible.Visible => "visible",
        _ => "none",
    };

    /// <summary>
    /// The spell's own data wins: caster-chosen modes (by <c>parameters.mode</c>, "both" when it's missing or unknown), then
    /// <see cref="SpellDefinition.Perceptible"/>. Without either, it's derived: an illusion is seen (or heard, by mode), area damage
    /// is seen and heard, thunder is heard. A verbal component is speech, not a spectacle, so it never counts.
    /// Every generated SRD/ORC spell carries the field (scripts/generate_spells.py derives it with this same rule), so the
    /// derivation only runs for homebrew and plugin spells that don't.
    /// </summary>
    public static Perceptible OfSpell(SpellDefinition? spell, RulesetAction action)
    {
        var mode = action.Parameters.GetValueOrDefault("mode");
        if (spell?.PerceptibleModes is { Count: > 0 } modes)
        {
            var chosen = mode == null
                ? null
                : modes.FirstOrDefault(m => string.Equals(m.Key, mode.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
            return chosen != null ? Parse(chosen) : Perceptible.Both;
        }

        if (spell?.Perceptible != null)
        {
            return Parse(spell.Perceptible);
        }

        var result = Perceptible.None;
        if (string.Equals(spell?.School, "illusion", StringComparison.OrdinalIgnoreCase))
        {
            var byMode = Parse(mode);
            result |= byMode == Perceptible.None ? Perceptible.Both : byMode;
        }

        var damaging = spell?.DamageAtSlotLevel != null || spell?.DamageAtCharacterLevel != null || spell?.DamagePools != null
                       || action.Parameters.ContainsKey("damageDice");
        if (damaging && spell?.AreaOfEffectType != null)
        {
            result |= Perceptible.Both;
        }

        if (string.Equals(spell?.DamageType ?? action.DamageType, "thunder", StringComparison.OrdinalIgnoreCase))
        {
            result |= Perceptible.Loud;
        }

        return result;
    }
}

/// <summary>
/// Witness channel (WITNESS_CHANNEL_PLAN.md): when a beat is loud or visible, name everyone else at the actor's location who
/// perceived it, so the narrator gives the camp, the captives and the passers-by a reaction instead of narrating only the fight.
/// Separate from the one-NPC initiative slot, which can't carry a whole crowd. Combatants are left out (they get their own turns),
/// and so are PCs (the player decides what they do).
/// </summary>
internal sealed class WitnessContextContributor(SpellDefinitionProvider spells, CampaignDocumentKeys keys) : IContextContributor
{
    private const int MaxNames = 6;

    /// <summary>Who can't perceive at all, and which sense a condition takes away. Core rules shared by every creature, so code, not data.</summary>
    private static readonly HashSet<string> Insensible = new(StringComparer.OrdinalIgnoreCase) { "unconscious", "petrified", "paralyzed" };

    private sealed record Trigger(
        string ActorId, string Cause, Perceptible Signal, IReadOnlyCollection<string> Excluded, bool CombatNoise, string? LocationId = null);

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        var triggers = Triggers(turn).ToList();
        if (triggers.Count == 0)
        {
            return [];
        }

        var encounter = await turn.Session.LoadAsync<CombatEncounter>(keys.CombatCurrent(turn.CampaignName), ct);
        var combatantIds = encounter is { IsActive: true }
            ? encounter.Combatants.Select(c => c.CharacterId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var items = new List<ContextItem>();
        foreach (var group in (await WithLocationsAsync(turn, triggers, ct)).GroupBy(t => t.LocationId, StringComparer.OrdinalIgnoreCase))
        {
            var located = group.Select(t => t.Trigger).ToList();
            // Fighting noise is said once per fight; a spell, a death or an event is said every beat it happens.
            var spectacle = located.Where(t => !t.CombatNoise).ToList();
            var said = spectacle.Count > 0 ? spectacle : [located[0]];
            var signal = said.Aggregate(Perceptible.None, (s, t) => s | t.Signal);
            var excluded = new HashSet<string>(said.SelectMany(t => t.Excluded.Append(t.ActorId)), StringComparer.OrdinalIgnoreCase);
            excluded.UnionWith(combatantIds);

            var line = await WitnessLineAsync(turn, group.Key, said, signal, excluded, ct);
            if (line == null)
            {
                continue;
            }

            var key = spectacle.Count > 0
                ? $"witness:{turn.WorldSequence}:{group.Key}"
                : $"witness:combat:{group.Key}:{string.Join(",", combatantIds.Order(StringComparer.OrdinalIgnoreCase))}";
            items.Add(new ContextItem(key, line, 90));
        }

        return items;
    }

    private IEnumerable<Trigger> Triggers(ContextTurn turn)
    {
        var system = turn.Config?.ActiveSystem;
        foreach (var change in turn.AppliedChanges)
        {
            switch (change)
            {
                case RulesetAction { ActionType: RulesetActionType.Spell } cast:
                {
                    SpellDefinition? spell = null;
                    if (system != null && !string.IsNullOrWhiteSpace(cast.ActionName))
                    {
                        spells.TryGet(system, CastingComponentGate.NormalizeSlug(cast.ActionName), out spell);
                    }

                    var signal = Perceptibility.OfSpell(spell, cast);
                    if (signal != Perceptible.None)
                    {
                        yield return new Trigger(cast.CharacterId, cast.ActionName.Replace('_', ' ').ToLowerInvariant(), signal, cast.TargetIds, false);
                    }

                    break;
                }
                case RulesetAction { ActionType: RulesetActionType.Attack } attack:
                    yield return new Trigger(attack.CharacterId, "fighting", Perceptible.Both, attack.TargetIds, true);
                    break;
                case DeathChange { Revive: false } death:
                    yield return new Trigger(death.CharacterId, "dies", Perceptible.Visible, [death.KillerId ?? ""], false);
                    break;
                case EventOccurred { Perceptible: { } p } ev when Perceptibility.Parse(p) != Perceptible.None:
                    var actor = ev.InitiatorId ?? ev.Involved?.FirstOrDefault() ?? "";
                    yield return new Trigger(actor, Short(ev.Summary), Perceptibility.Parse(p), ev.Involved ?? [], false, ev.LocationId);
                    break;
            }
        }
    }

    /// <summary>Each trigger happens where its actor is (not where the party is: a split party matters), falling back to the event's
    /// own location and then the party's.</summary>
    private static async Task<List<(Trigger Trigger, string LocationId)>> WithLocationsAsync(
        ContextTurn turn, List<Trigger> triggers, CancellationToken ct)
    {
        var located = new List<(Trigger, string)>();
        foreach (var trigger in triggers)
        {
            var actor = string.IsNullOrEmpty(trigger.ActorId) ? null : await turn.LoadCharacterAsync(trigger.ActorId, ct);
            var locationId = trigger.LocationId ?? actor?.CurrentLocationId ?? turn.PartyLocationId;
            if (!string.IsNullOrEmpty(locationId))
            {
                located.Add((trigger with { ActorId = actor?.Id ?? trigger.ActorId, Cause = Cause(trigger, actor) }, locationId));
            }
        }

        return located;
    }

    private static string Cause(Trigger trigger, Character? actor) =>
        trigger.CombatNoise ? "a fight breaks out"
        : trigger.Cause == "dies" ? $"{actor?.Name ?? "someone"} dies"
        : actor != null && !trigger.Cause.Contains(actor.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{trigger.Cause} by {actor.Name}"
            : trigger.Cause;

    private static async Task<string?> WitnessLineAsync(ContextTurn turn, string locationId, List<Trigger> said, Perceptible signal,
        HashSet<string> excluded, CancellationToken ct)
    {
        var here = await turn.Session.Query<Character>()
            .Where(c => c.CampaignName == turn.CampaignName && c.CurrentLocationId == locationId && !c.IsPc && c.Death == null)
            .Take(25)
            .ToListAsync(ct);

        var witnesses = new List<string>();
        foreach (var c in here.Where(c => !excluded.Contains(c.Id))
                     .OrderByDescending(c => c.IsPartyCompanion || c.KeepAlive)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var conditions = (c.SystemStats?.StatusEffects ?? [])
                .Select(e => e.ConditionName ?? e.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (conditions.Overlaps(Insensible))
            {
                continue;
            }

            var perceived = signal;
            if (conditions.Contains("blinded"))
            {
                perceived &= ~Perceptible.Visible;
            }

            if (conditions.Contains("deafened"))
            {
                perceived &= ~Perceptible.Loud;
            }

            if (perceived == Perceptible.None)
            {
                continue;
            }

            witnesses.Add(perceived == signal || signal != Perceptible.Both
                ? c.Name
                : $"{c.Name} ({(perceived == Perceptible.Loud ? "blinded: heard it only" : "deafened: saw it only")})");
        }

        if (witnesses.Count == 0)
        {
            return null;
        }

        var names = witnesses.Count > MaxNames
            ? string.Join(", ", witnesses.Take(MaxNames)) + $" +{witnesses.Count - MaxNames} others"
            : string.Join(", ", witnesses);
        var causes = string.Join("; ", said.Select(t => t.Cause).Distinct(StringComparer.OrdinalIgnoreCase));
        return $"Witnesses ({causes}, {Perceptibility.Describe(signal)}): {names}. "
               + "Give each a short reaction in this response, even mid-combat.";
    }

    private static string Short(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "something" : text.Length <= 60 ? text.TrimEnd('.') : text[..57] + "...";
}
