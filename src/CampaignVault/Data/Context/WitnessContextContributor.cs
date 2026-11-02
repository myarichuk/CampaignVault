using CampaignVault.Data.Templates;
using CampaignVault.Data.Witness;
using CampaignVault.Models;

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
/// and so are PCs (the player decides what they do). Who is there and what they perceived is <see cref="IWitnessSelector"/>'s call.
/// A loud beat also reaches one exit further (W5): a second, shorter line names who heard it from next door.
/// </summary>
internal sealed class WitnessContextContributor(PerceptibleBeats beats, IWitnessSelector selector, CampaignDocumentKeys keys) : IContextContributor
{
    private const int MaxNames = 6;

    public async Task<IEnumerable<ContextItem>> ContributeAsync(ContextTurn turn, CancellationToken ct = default)
    {
        var triggers = beats.Detect(turn.AppliedChanges, turn.Config?.ActiveSystem).ToList();
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
            var located = group.ToList();
            // A spell, a death or an event is said every beat it happens. Fighting noise is said once per fight (keyed on its
            // combatants), and outside combat, where there's no fight to key on, every beat. When the first blow lands in the same
            // beat as a spectacle, both causes share one line under the fight's key, so the fight isn't announced again next round.
            var spectacle = located.Where(t => !t.Beat.CombatNoise).ToList();
            var noise = located.FirstOrDefault(t => t.Beat.CombatNoise);
            var fightKey = combatantIds.Count > 0
                ? $"witness:combat:{group.Key}:{string.Join(",", combatantIds.Order(StringComparer.OrdinalIgnoreCase))}"
                : null;
            var fightIsNews = noise != null && (fightKey == null || !turn.DeliveredContextKeys.Contains(fightKey));
            List<Located> said = fightIsNews ? [.. spectacle, noise!] : spectacle;
            if (said.Count == 0)
            {
                continue;
            }

            var signal = said.Aggregate(Perceptible.None, (s, t) => s | t.Beat.Signal);
            var excluded = new HashSet<string>(said.SelectMany(t => t.Beat.Excluded.Append(t.Beat.ActorId)), StringComparer.OrdinalIgnoreCase);
            excluded.UnionWith(combatantIds);

            var key = fightIsNews && fightKey != null ? fightKey : $"witness:{turn.WorldSequence}:{group.Key}";

            if (await WitnessLineAsync(turn, group.Key, said, signal, excluded, ct) is { } line)
            {
                items.Add(new ContextItem(key, line, 90));
            }

            if (await EarshotLineAsync(turn, group.Key, said, signal, excluded, ct) is { } near)
            {
                items.Add(new ContextItem(key + ":near", near, 75));
            }
        }

        return items;
    }

    private sealed record Located(PerceptibleBeat Beat, string Cause, Character? Actor, string LocationId);

    /// <summary>Each beat happens where its actor is (not where the party is: a split party matters), falling back to the event's
    /// own location and then the party's.</summary>
    private static async Task<List<Located>> WithLocationsAsync(ContextTurn turn, List<PerceptibleBeat> triggers, CancellationToken ct)
    {
        var located = new List<Located>();
        foreach (var beat in triggers)
        {
            var actor = string.IsNullOrEmpty(beat.ActorId) ? null : await turn.LoadCharacterAsync(beat.ActorId, ct);
            var locationId = beat.Where(actor, turn.PartyLocationId);
            if (!string.IsNullOrEmpty(locationId))
            {
                located.Add(new Located(beat with { ActorId = actor?.Id ?? beat.ActorId }, Cause(beat, actor), actor, locationId));
            }
        }

        return located;
    }

    private static string Cause(PerceptibleBeat beat, Character? actor) =>
        beat.CombatNoise ? "a fight breaks out"
        : beat.Cause == "dies" ? $"{actor?.Name ?? "someone"} dies"
        : actor != null && !beat.Cause.Contains(actor.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{beat.Cause} by {actor.Name}"
            : beat.Cause;

    private async Task<string?> WitnessLineAsync(ContextTurn turn, string locationId, List<Located> said, Perceptible signal,
        HashSet<string> excluded, CancellationToken ct)
    {
        // The source the witnesses look at: an invisible caster's effect is seen, the caster isn't. A death has no source to hide.
        var source = said.Select(t => t.Actor).FirstOrDefault(a => a != null && said.Any(t => t.Actor == a && t.Beat.Cause != "dies"));
        var witnesses = (await selector.AtAsync(turn.Session, turn.CampaignName, locationId, signal, source, excluded, ct))
            .Select(w => w.Notice.Note == null ? w.Character.Name : $"{w.Character.Name} ({w.Notice.Note})")
            .ToList();
        if (witnesses.Count == 0)
        {
            return null;
        }

        var causes = string.Join("; ", said.Select(t => t.Cause).Distinct(StringComparer.OrdinalIgnoreCase));
        var drawing = said.Any(t => t.Beat.Draws) ? ", drawing attention" : "";
        return $"Witnesses ({causes}, {Perceptibility.Describe(signal)}{drawing}): {Names(witnesses)}. "
               + "Give each a short reaction in this response, even mid-combat.";
    }

    /// <summary>W5: who heard a loud beat from one exit away. They don't see it, so the cause is the noise, not who made it.</summary>
    private async Task<string?> EarshotLineAsync(ContextTurn turn, string locationId, List<Located> said, Perceptible signal,
        HashSet<string> excluded, CancellationToken ct)
    {
        var loud = said.Where(t => (t.Beat.Signal & Perceptible.Loud) != 0).ToList();
        if (loud.Count == 0)
        {
            return null;
        }

        var near = await selector.InEarshotAsync(turn.Session, turn.CampaignName, locationId, signal, excluded, ct);
        var heard = near.SelectMany(n => n.Heard.Select(w => $"{w.Character.Name} at {n.Where.Name}")).ToList();
        if (heard.Count == 0)
        {
            return null;
        }

        var here = await turn.Session.LoadAsync<Location>(locationId, ct);
        var sounds = string.Join("; ", loud.Select(t => t.Beat.CombatNoise ? "fighting" : t.Beat.Cause).Distinct(StringComparer.OrdinalIgnoreCase));
        return $"Heard from {here?.Name ?? "nearby"} ({sounds}): {Names(heard)}. They hear it but can't see it; a short reaction each if it fits.";
    }

    private static string Names(List<string> names) =>
        names.Count > MaxNames
            ? string.Join(", ", names.Take(MaxNames)) + $" +{names.Count - MaxNames} others"
            : string.Join(", ", names);
}
