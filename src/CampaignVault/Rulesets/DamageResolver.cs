using System.Globalization;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>Which system's order of adjustments <see cref="DamageResolver"/> follows.</summary>
internal enum DamageOrdering
{
    /// <summary>SRD 5.1: flat reductions, then resistance (once), then vulnerability. Immunity wins.</summary>
    Dnd5e,

    /// <summary>Player Core: immunity, then weakness, then resistance.</summary>
    Pf2e,
}

/// <summary>
/// One amount of one damage type. <paramref name="Primary"/> marks the hit's own damage: the flat reduction a feat gives
/// against an attack is taken off that packet once, not off every rider and extra die.
/// </summary>
internal readonly record struct DamagePacket(int Amount, string? Type, bool Primary = false);

/// <summary>
/// The damage of a save spell, rolled once per cast on the first target that needs it and then shared, so every target
/// that fails sees the same number (the dice are the spell's, not the target's).
/// </summary>
internal sealed class SaveDamageRoll
{
    public bool IsRolled { get; set; }

    public IReadOnlyList<DamagePacket> Packets { get; set; } = [];

    /// <summary>A parenthesised breakdown of the pools for the narrative, or empty.</summary>
    public string Detail { get; set; } = "";
}

/// <summary>The damage a target takes: the total, each packet after mitigation, and what to print.</summary>
internal sealed record DamageResult(int Total, IReadOnlyList<int> PerPacket, IReadOnlyList<string> Notes)
{
    public static readonly DamageResult None = new(0, [], []);
}

/// <summary>
/// The one place that turns "N damage of type T" into what a target takes. Every damage path (attack, splash, extra dice,
/// rider, save spells, delayed ticks) feeds packets here, so a stat-block resistance and a feat's resistance (Rage) halve
/// once between them, and a save path can't skip a trait. It reads the stat block (<c>DamageModifiers</c> for immunity,
/// resistance and vulnerability; <c>DamageResistances</c> for flat values, a negative one being a weakness) and the feat
/// effects (<c>resistance</c>, <c>damageReduction</c>). Type lookups ignore case.
/// </summary>
internal static class DamageResolver
{
    /// <summary>What an unspecified damage type counts as when the stat block is read.</summary>
    private const string UntypedKey = "Physical";

    public static DamageResult Resolve(
        IReadOnlyList<DamagePacket> packets,
        SystemExtension targetStats,
        IReadOnlyList<ActiveFeatEffect> targetEffects,
        RulesetAction? action,
        DamageOrdering ordering)
    {
        if (packets.Count == 0)
        {
            return DamageResult.None;
        }

        var notes = new List<string>();
        var per = new List<int>(packets.Count);
        foreach (var packet in packets)
        {
            per.Add(ResolveOne(packet, targetStats, targetEffects, action, ordering, notes));
        }

        return new DamageResult(per.Sum(), per, notes);
    }

    /// <summary>One packet, for callers (splash, a delayed tick) that have just the one.</summary>
    public static int Resolve(
        int amount,
        string? type,
        SystemExtension targetStats,
        IReadOnlyList<ActiveFeatEffect> targetEffects,
        RulesetAction? action,
        DamageOrdering ordering,
        List<string>? notes = null)
    {
        var result = Resolve([new DamagePacket(amount, type, Primary: true)], targetStats, targetEffects, action, ordering);
        notes?.AddRange(result.Notes);
        return result.Total;
    }

    private static int ResolveOne(
        DamagePacket packet,
        SystemExtension stats,
        IReadOnlyList<ActiveFeatEffect> effects,
        RulesetAction? action,
        DamageOrdering ordering,
        List<string> notes)
    {
        var damage = Math.Max(0, packet.Amount);
        if (damage == 0)
        {
            return 0;
        }

        var label = string.IsNullOrWhiteSpace(packet.Type) ? "damage" : $"{packet.Type} damage";
        var statKey = string.IsNullOrWhiteSpace(packet.Type) ? UntypedKey : packet.Type;
        var multiplier = Lookup(stats.DamageModifiers, statKey);
        var flat = Lookup(stats.DamageResistances, statKey) ?? 0;

        if (multiplier is <= 0f)
        {
            notes.Add($"immune to {label}");
            return 0;
        }

        var featResists = effects.Count > 0 && FeatEffectRules.ResistsDamage(effects, packet.Type, action, notes);
        var featReduction = packet.Primary && action is not null && effects.Count > 0
            ? FeatEffectRules.DamageReduction(effects, packet.Type, action, notes)
            : 0;

        var resolved = ordering == DamageOrdering.Pf2e
            ? ResolvePf2e(damage, multiplier, flat, featResists, featReduction)
            : ResolveDnd5e(damage, multiplier, flat, featResists, featReduction);

        if (resolved != damage)
        {
            notes.Add($"{label} {damage} -> {resolved}");
        }

        return resolved;
    }

    private static int ResolveDnd5e(int damage, float? multiplier, int flat, bool featResists, int featReduction)
    {
        damage = Math.Max(0, damage - flat - featReduction);

        // Resistance applies once however many sources grant it: the stat block's multiplier and a feat halve together.
        var resistance = multiplier is > 0f and < 1f ? multiplier.Value : 1f;
        if (featResists)
        {
            resistance = Math.Min(resistance, 0.5f);
        }

        damage = (int)Math.Floor(damage * resistance);

        // Vulnerability follows resistance; a creature with both takes the halved amount doubled.
        return multiplier is > 1f ? (int)Math.Floor(damage * multiplier.Value) : damage;
    }

    private static int ResolvePf2e(int damage, float? multiplier, int flat, bool featResists, int featReduction)
    {
        // A multiplier is the older way to write "resistant" or "vulnerable"; honor it before the flat adjustments.
        if (multiplier is { } m && m != 1f)
        {
            damage = (int)Math.Floor(damage * m);
        }

        if (damage <= 0)
        {
            return 0;
        }

        // Weakness (a negative flat value) adds to damage that got through; resistance takes off, to a floor of 0.
        if (flat < 0)
        {
            damage += -flat;
        }

        var resistance = Math.Max(flat, 0) + featReduction;
        damage = Math.Max(0, damage - resistance);
        return featResists ? damage / 2 : damage;
    }

    private static T? Lookup<T>(IReadOnlyDictionary<string, T> map, string key)
        where T : struct
    {
        foreach (var (candidate, value) in map)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The culture-invariant parse every damage path uses for a <c>damageBonus</c> parameter.</summary>
    public static bool TryParseBonus(string? text, out int bonus) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out bonus);
}
