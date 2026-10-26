using System.Globalization;
using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Default implementation of <see cref="IRollService"/> using <see cref="Random.Shared"/>.
/// Registered as a Singleton in production because Random.Shared is thread-safe.
/// For deterministic test scenarios, override this registration in the test's DI scope 
/// with a custom mock or seeded implementation.
/// </summary>
public sealed class DefaultRollService : IRollService
{
    // Matches: optional [N]dX, optional flat modifier. e.g. "1d20+5", "d20", "2d6", "3d8-1", "5".
    // Whitespace is stripped before matching, so "1d20 + 5" and "2d6 +1" parse too; a bare constant
    // ("5", a fixed-damage effect) is a flat modifier with no dice.
    private static readonly Regex DiceRegex =
        new(@"^(?:(\d*)d(\d+))?([+-]?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Bounds on one expression: a mistyped "100000d6" from the model must not allocate a 100k-element list.</summary>
    internal const int MaxDiceCount = 1000;
    internal const int MaxDieSides = 10_000;

    private readonly Random _rng;

    public DefaultRollService() : this(Random.Shared)
    {
    }

    /// <summary>Inject a seeded Random for deterministic test scenarios.</summary>
    public DefaultRollService(Random rng) => _rng = rng;

    // ── Public interface ──────────────────────────────────────────────────────

    public Task<RollOutcome> RollAsync(RollRequest request, CancellationToken ct = default)
        => Task.FromResult(Evaluate(request));

    public Task<IReadOnlyList<RollOutcome>> RollBatchAsync(
        IEnumerable<RollRequest> requests,
        CancellationToken ct = default)
    {
        IReadOnlyList<RollOutcome> results = [.. requests.Select(Evaluate)];
        return Task.FromResult(results);
    }


    // ── Core evaluation ───────────────────────────────────────────────────────

    private RollOutcome Evaluate(RollRequest req)
    {
        return req.Mechanic switch
        {
            DiceMechanic.Standard => EvaluateStandard(req),
            DiceMechanic.Advantage => EvaluateAdvantage(req, keepHigh: true),
            DiceMechanic.Disadvantage => EvaluateAdvantage(req, keepHigh: false),
            DiceMechanic.Explosive => EvaluateExplosive(req),
            DiceMechanic.KeepHighest => EvaluateKeep(req, keepHigh: true),
            DiceMechanic.KeepLowest => EvaluateKeep(req, keepHigh: false),
            DiceMechanic.RollUnder => EvaluateRollUnder(req),
            _ => throw new ArgumentOutOfRangeException(nameof(req.Mechanic), req.Mechanic, "Unknown DiceMechanic")
        };
    }

    // ── Standard ──────────────────────────────────────────────────────────────

    private RollOutcome EvaluateStandard(RollRequest req)
    {
        var (diceCount, dieSides, flatMod) = ParseExpression(req.Expression);
        var dice = RollDice(diceCount, dieSides);
        var total = dice.Sum() + flatMod + req.Bonus;

        var isCrit = dieSides == 20 && dice.Any(d => d == 20);
        var isComplication = dieSides == 20 && dice.Any(d => d == 1);

        return new RollOutcome
        {
            Tag = req.Tag,
            Result = total,
            IndividualDice = dice,
            HasCritical = isCrit,
            HasComplication = isComplication,
            Summary = BuildSummary(dice, flatMod, req.Bonus, total, suffix: null)
        };
    }

    // ── Advantage / Disadvantage ──────────────────────────────────────────────

    private RollOutcome EvaluateAdvantage(RollRequest req, bool keepHigh)
    {
        var (diceCount, dieSides, flatMod) = ParseExpression(req.Expression);
        // Roll twice the pool, split in half, keep the better set
        var first = RollDice(diceCount, dieSides);
        var second = RollDice(diceCount, dieSides);

        var keptSet = keepHigh
            ? (first.Sum() >= second.Sum() ? first : second)
            : (first.Sum() <= second.Sum() ? first : second);
        var droppedSet = ReferenceEquals(keptSet, first) ? second : first;

        var total = keptSet.Sum() + flatMod + req.Bonus;
        var isCrit = dieSides == 20 && keptSet.Any(d => d == 20);
        var isComplication = dieSides == 20 && keptSet.Any(d => d == 1);
        var label = keepHigh ? "Advantage" : "Disadvantage";

        return new RollOutcome
        {
            Tag = req.Tag,
            Result = total,
            IndividualDice = keptSet,
            HasCritical = isCrit,
            HasComplication = isComplication,
            Summary =
                $"{BuildSummary(keptSet, flatMod, req.Bonus, total, suffix: null)} ({label}: kept {keptSet.Sum()} over {droppedSet.Sum()})"
        };
    }

    // ── Explosive ─────────────────────────────────────────────────────────────

    private const int MaxExplosiveExtraRolls = 20;

    private RollOutcome EvaluateExplosive(RollRequest req)
    {
        var (diceCount, dieSides, flatMod) = ParseExpression(req.Expression);
        var allDice = new List<int>();
        var total = 0;

        for (var i = 0; i < diceCount; i++)
        {
            int roll;
            var extraRolls = 0;
            do
            {
                roll = _rng.Next(1, dieSides + 1);
                allDice.Add(roll);
                total += roll;
                extraRolls++;
            } while (roll == dieSides && extraRolls < MaxExplosiveExtraRolls); // keep rolling on max
        }

        total += flatMod + req.Bonus;

        return new RollOutcome
        {
            Tag = req.Tag,
            Result = total,
            IndividualDice = allDice,
            HasCritical = allDice.Any(d => d == dieSides),
            HasComplication = false,
            Summary = $"Explosive {BuildSummary(allDice, flatMod, req.Bonus, total, suffix: "(chained)")}"
        };
    }

    // ── KeepHighest / KeepLowest ──────────────────────────────────────────────

    private RollOutcome EvaluateKeep(RollRequest req, bool keepHigh)
    {
        var (diceCount, dieSides, flatMod) = ParseExpression(req.Expression);
        var keep = req.Keep ?? diceCount;
        var dice = RollDice(diceCount, dieSides);
        var sorted = keepHigh
            ? dice.OrderByDescending(d => d).Take(keep).ToList()
            : dice.OrderBy(d => d).Take(keep).ToList();
        var total = sorted.Sum() + flatMod + req.Bonus;

        return new RollOutcome
        {
            Tag = req.Tag,
            Result = total,
            IndividualDice = sorted,
            HasCritical = dieSides == 20 && sorted.Any(d => d == 20),
            HasComplication = dieSides == 20 && sorted.Any(d => d == 1),
            Summary =
                $"Keep{(keepHigh ? "High" : "Low")} {BuildSummary(sorted, flatMod, req.Bonus, total, suffix: $"from {diceCount}d{dieSides}")}"
        };
    }

    // ── RollUnder ─────────────────────────────────────────────────────────────

    private RollOutcome EvaluateRollUnder(RollRequest req)
    {
        var (diceCount, dieSides, _) = ParseExpression(req.Expression);
        var tn = req.TargetNumber ??
                 throw new InvalidOperationException($"RollUnder requires TargetNumber (tag: {req.Tag})");
        var dice = RollDice(diceCount, dieSides);
        var result = dice.Sum(); // for single-die, just the die; for pools the total
        var success = result <= tn;

        return new RollOutcome
        {
            Tag = req.Tag,
            Result = result,
            IndividualDice = dice,
            IsSuccess = success,
            Summary =
                $"RollUnder: [{string.Join(", ", dice)}] = {result} vs TN {tn} → {(success ? "Success" : "Failure")}"
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<int> RollDice(int count, int sides)
    {
        if (sides <= 0)
        {
            return [0]; // flat-only expression (no dice)
        }

        return
        [
            .. Enumerable.Range(0, count)
                .Select(_ => _rng.Next(1, sides + 1))
        ];
    }

    internal static (int Count, int Sides, int FlatMod) ParseExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            throw new ArgumentException("Cannot parse dice expression: it is empty. Use the form NdX[+M], e.g. '1d20+5'.");
        }

        var compact = string.Concat(expr.Where(c => !char.IsWhiteSpace(c)));
        var m = DiceRegex.Match(compact);
        if (!m.Success)
        {
            throw new ArgumentException($"Cannot parse dice expression: '{expr}'. Use the form NdX[+M], e.g. '1d20+5' or '2d6-1'.");
        }

        var count = m.Groups[1].Success && m.Groups[1].Length > 0
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : 1;
        var sides = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var flatMod = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;

        if (count > MaxDiceCount || sides > MaxDieSides)
        {
            throw new ArgumentException(
                $"Dice expression '{expr}' is out of range: at most {MaxDiceCount} dice of at most d{MaxDieSides}.");
        }

        return (count, sides, flatMod);
    }

    private static string BuildSummary(List<int> dice, int flatMod, int bonus, int total, string? suffix)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[').AppendJoin(", ", dice).Append(']');
        if (flatMod != 0)
        {
            sb.Append(flatMod > 0 ? $"+{flatMod}" : $"{flatMod}");
        }

        if (bonus != 0)
        {
            sb.Append(bonus > 0 ? $"+{bonus}" : $"{bonus}");
        }

        sb.Append($" = {total}");
        if (suffix is not null)
        {
            sb.Append($" ({suffix})");
        }

        return sb.ToString();
    }
}