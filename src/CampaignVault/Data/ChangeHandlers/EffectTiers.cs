using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// The guardrails behind <see cref="ApplyEffectChange"/>: what each tier may do. Pure, so the same numbers are tested
/// and reused by plugin-side engine tiers.
/// </summary>
public static class EffectTiers
{
    public sealed record Tier(string Name, float MaxMagnitude, double? MaxHours, float MaxSpeedFeet, bool NeedsRecoveryHint);

    public const string Persistent = "persistent";

    /// <summary>Most non-persistent buffs (and, separately, debuffs) a character may carry from events at once.</summary>
    public const int MaxEventEffectsPerSide = 2;

    public static readonly IReadOnlyList<Tier> All =
    [
        new("light", 1, 1, 0, false),
        new("moderate", 2, 8, 10, false),
        new("serious", 3, 24, 20, true),
        new(Persistent, 3, null, 20, false),
    ];

    /// <summary>What the engine understands as an effect's target; anything else is refused rather than guessed.</summary>
    public static readonly IReadOnlySet<string> Stats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AllChecks", "AllSaves", "AttackRoll", "AllRolls", "Initiative", "Speed",
        "Acrobatics", "AnimalHandling", "Arcana", "Athletics", "Deception", "History", "Insight", "Intimidation",
        "Investigation", "Medicine", "Nature", "Perception", "Performance", "Persuasion", "Religion", "SleightOfHand",
        "Stealth", "Survival",
    };

    public static Tier? Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsBuff(StatusEffect e) => e.StatModifiers.Where(kv => kv.Key != ActionBlock.Tag).Sum(kv => kv.Value) > 0;

    /// <summary>
    /// Validates and clamps a request. On success <paramref name="modifiers"/> holds the clamped values and
    /// <paramref name="hours"/> the kept duration (null for persistent); <paramref name="notes"/> says what was clamped.
    /// </summary>
    public static bool TryClamp(
        ApplyEffectChange req,
        out Tier tier,
        out Dictionary<string, float> modifiers,
        out double? hours,
        out List<string> notes,
        out string? error)
    {
        modifiers = [];
        hours = null;
        notes = [];
        error = null;
        tier = All[0];

        if (Find(req.Tier) is not { } found)
        {
            error = "tier must be light, moderate, serious or persistent.";
            return false;
        }

        tier = found;
        var valence = req.Valence?.Trim().ToLowerInvariant();
        if (valence is not ("buff" or "debuff"))
        {
            error = "valence must be buff or debuff.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(req.Key) || string.IsNullOrWhiteSpace(req.Name))
        {
            error = "key and name are required.";
            return false;
        }

        if (req.Modifiers.Count is 0 or > 2)
        {
            error = "modifiers needs 1 or 2 entries.";
            return false;
        }

        foreach (var (rawStat, value) in req.Modifiers)
        {
            var stat = Stats.FirstOrDefault(s => string.Equals(s, rawStat?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (stat is null)
            {
                error = $"'{rawStat}' is not an effect target. Use: {string.Join(", ", Stats)}.";
                return false;
            }

            if (value == 0 || float.IsNaN(value) || float.IsInfinity(value))
            {
                error = $"modifier for {stat} must be a non-zero number.";
                return false;
            }

            if ((valence == "buff") != (value > 0))
            {
                error = $"a {valence} modifier must be {(valence == "buff" ? "positive" : "negative")} ({stat} {value}).";
                return false;
            }

            var isSpeed = stat.Equals("Speed", StringComparison.OrdinalIgnoreCase);
            var cap = isSpeed ? tier.MaxSpeedFeet : tier.MaxMagnitude;
            if (cap <= 0)
            {
                error = $"{stat} needs tier moderate or higher.";
                return false;
            }

            var clamped = Math.Clamp(value, -cap, cap);
            if (clamped != value)
                notes.Add($"{stat} requested {value:+0.##;-0.##}, clamped to {clamped:+0.##;-0.##} for {tier.Name}");
            modifiers[stat] = clamped;
        }

        if (tier.Name == Persistent)
        {
            if (string.IsNullOrWhiteSpace(req.ImposedBy) || string.IsNullOrWhiteSpace(req.Removal))
            {
                error = "persistent effects need imposedBy and removal (what imposes it, what ends it).";
                return false;
            }

            return true;
        }

        if (req.DurationHours is not > 0)
        {
            error = $"durationHours is required for tier {tier.Name} (at most {tier.MaxHours}h).";
            return false;
        }

        if (tier.NeedsRecoveryHint && string.IsNullOrWhiteSpace(req.RecoveryHint))
        {
            error = "serious effects need a recoveryHint (rest, treatment, a save).";
            return false;
        }

        hours = Math.Min(req.DurationHours.Value, tier.MaxHours!.Value);
        if (hours < req.DurationHours)
            notes.Add($"duration requested {req.DurationHours}h, clamped to {hours}h for {tier.Name}");
        return true;
    }
}
