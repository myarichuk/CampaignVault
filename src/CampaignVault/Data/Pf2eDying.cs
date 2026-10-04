using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>How a PF2e recovery check turned out (Player Core, "Recovery Checks").</summary>
internal enum RecoveryDegree
{
    CriticalFailure,
    Failure,
    Success,
    CriticalSuccess,
}

/// <summary>
/// The PF2e dying, wounded and doomed counters on a player character. Each is a status named "Dying 2" (the same
/// "Name N" convention exhaustion and frightened use) whose condition definition carries the rules text; the numbers
/// are read and written only here. Reaching 0 hit points gives dying 1 (2 from a critical hit) plus the wounded value,
/// damage while dying adds 1 (2), and dying 4 minus the doomed value is death. Rising above 0 ends dying and adds one
/// to wounded.
/// </summary>
internal static class Pf2eDying
{
    public const string Dying = "dying";
    public const string Wounded = "wounded";
    public const string Doomed = "doomed";

    /// <summary>Dying 4 kills; each point of doomed lowers that by one.</summary>
    public const int DeathValue = 4;

    /// <summary>The PF2e player characters these rules apply to (NPC death stays an explicit commit).</summary>
    public static bool Applies(Character character) => character.IsPc && character.SystemStats is Pf2eExtension;

    public static int Get(Character character, string condition)
    {
        var level = 0;
        foreach (var status in Matching(character, condition))
        {
            level = Math.Max(level, LevelOf(status));
        }

        return level;
    }

    /// <summary>Sets the counter; zero or less removes it. Keeps one status per condition.</summary>
    public static void Set(Character character, string condition, int value)
    {
        if (character.SystemStats?.StatusEffects is not { } effects)
        {
            return;
        }

        var existing = Matching(character, condition).ToList();
        foreach (var extra in existing.Skip(1))
        {
            effects.Remove(extra);
        }

        if (value <= 0)
        {
            if (existing.Count > 0)
            {
                effects.Remove(existing[0]);
            }

            return;
        }

        var name = $"{char.ToUpperInvariant(condition[0])}{condition[1..]} {value}";
        if (existing.Count > 0)
        {
            existing[0].Name = name;
            return;
        }

        effects.Add(new StatusEffect { Name = name, Category = "Condition", ConditionName = condition });
    }

    public static int DeathThreshold(Character character) => Math.Max(1, DeathValue - Get(character, Doomed));

    public static bool IsDeadlyValue(Character character, int dying) => dying >= DeathThreshold(character);

    /// <summary>The dying value for being knocked to 0 hit points: 1 (2 from a critical hit or critical failure) plus the wounded value.</summary>
    public static int ValueWhenDowned(Character character, bool critical) => (critical ? 2 : 1) + Get(character, Wounded);

    /// <summary>
    /// Dying ends because hit points rose above 0: remove it and add one to wounded. Returns the new wounded value, or
    /// null when the character was not dying.
    /// </summary>
    public static int? Recover(Character character)
    {
        if (Get(character, Dying) <= 0)
        {
            return null;
        }

        Set(character, Dying, 0);
        var wounded = Get(character, Wounded) + 1;
        Set(character, Wounded, wounded);
        return wounded;
    }

    /// <summary>A flat check against DC 10 + dying. Beating it by 10 is a critical success, missing by 10 a critical failure; a natural 20 or 1 moves it one step.</summary>
    public static RecoveryDegree Degree(int die, int dying)
    {
        var margin = die - (10 + dying);
        var degree = margin >= 10 ? RecoveryDegree.CriticalSuccess
            : margin >= 0 ? RecoveryDegree.Success
            : margin <= -10 ? RecoveryDegree.CriticalFailure
            : RecoveryDegree.Failure;
        if (die == 20 && degree != RecoveryDegree.CriticalSuccess)
        {
            degree++;
        }
        else if (die == 1 && degree != RecoveryDegree.CriticalFailure)
        {
            degree--;
        }

        return degree;
    }

    public static int DyingChange(RecoveryDegree degree) => degree switch
    {
        RecoveryDegree.CriticalSuccess => -2,
        RecoveryDegree.Success => -1,
        RecoveryDegree.Failure => 1,
        _ => 2,
    };

    private static IEnumerable<StatusEffect> Matching(Character character, string condition) =>
        (character.SystemStats?.StatusEffects ?? []).Where(e => Is(e, condition));

    private static bool Is(StatusEffect status, string condition)
    {
        if (string.Equals(status.ConditionName, condition, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = status.Name?.Trim() ?? "";
        var space = name.LastIndexOf(' ');
        var stem = space > 0 && int.TryParse(name[(space + 1)..], out _) ? name[..space] : name;
        return string.Equals(stem, condition, StringComparison.OrdinalIgnoreCase);
    }

    private static int LevelOf(StatusEffect status) =>
        ConditionExpiryEvaluator.TryParseStackLevel(status.Name?.Trim() ?? "", out var level) && level > 0 ? level : 1;
}
