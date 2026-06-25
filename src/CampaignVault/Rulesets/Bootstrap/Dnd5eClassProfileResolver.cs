using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

internal static partial class Dnd5eClassProfileResolver
{
    [GeneratedRegex(@"(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLevelRegex();

    [GeneratedRegex(@"(?<class>barbarian|fighter|paladin|ranger|bard|cleric|druid|monk|rogue|warlock|artificer|wizard|sorcerer)\s*(?<level>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ClassLevelSegmentRegex();

    private static readonly Dictionary<string, int> ClassHitDieSides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["barbarian"] = 12,
        ["fighter"] = 10,
        ["paladin"] = 10,
        ["ranger"] = 10,
        ["bard"] = 8,
        ["cleric"] = 8,
        ["druid"] = 8,
        ["monk"] = 8,
        ["rogue"] = 8,
        ["warlock"] = 8,
        ["artificer"] = 8,
        ["wizard"] = 6,
        ["sorcerer"] = 6,
    };

    public static IReadOnlyList<ClassLevelEntry> ParseClassLevels(
        string? classLevel,
        IReadOnlyList<ClassLevelEntry>? structured = null)
    {
        if (structured is { Count: > 0 })
        {
            return structured
                .Where(e => e.Level > 0 && !string.IsNullOrWhiteSpace(e.Class))
                .Select(e => new ClassLevelEntry { Class = e.Class.Trim(), Level = e.Level })
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(classLevel))
        {
            return [];
        }

        var segments = classLevel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = new List<ClassLevelEntry>();

        foreach (var segment in segments)
        {
            var match = ClassLevelSegmentRegex().Match(segment);
            if (match.Success
                && int.TryParse(match.Groups["level"].Value, out var level)
                && level > 0)
            {
                entries.Add(new ClassLevelEntry
                {
                    Class = match.Groups["class"].Value,
                    Level = level,
                });
            }
        }

        if (entries.Count > 0)
        {
            return entries;
        }

        var trailing = TrailingLevelRegex().Match(classLevel);
        if (trailing.Success && int.TryParse(trailing.Groups[1].Value, out var singleLevel) && singleLevel > 0)
        {
            foreach (var (className, _) in ClassHitDieSides)
            {
                if (classLevel.Contains(className, StringComparison.OrdinalIgnoreCase))
                {
                    return [new ClassLevelEntry { Class = className, Level = singleLevel }];
                }
            }
        }

        return [];
    }

    public static int TotalLevel(IReadOnlyList<ClassLevelEntry> entries) =>
        entries.Count == 0 ? 0 : entries.Sum(e => e.Level);

    public static bool TryResolveHitDie(string className, out int dieSides) =>
        ClassHitDieSides.TryGetValue(className.Trim(), out dieSides);

    public static bool TryResolve(
        string? classLevel,
        string? hitDie,
        int? level,
        IReadOnlyList<ClassLevelEntry>? structured,
        out int resolvedLevel,
        out int dieSides)
    {
        resolvedLevel = level ?? 1;
        dieSides = 0;

        var classLevels = ParseClassLevels(classLevel, structured);
        if (classLevels.Count > 0)
        {
            resolvedLevel = Math.Max(1, TotalLevel(classLevels));
            dieSides = classLevels
                .Select(e => TryResolveHitDie(e.Class, out var sides) ? sides : 0)
                .DefaultIfEmpty(0)
                .Max();
            return dieSides > 0;
        }

        if (!string.IsNullOrWhiteSpace(hitDie) && TryParseDie(hitDie, out dieSides))
        {
            if (resolvedLevel < 1)
            {
                resolvedLevel = 1;
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(classLevel))
        {
            var match = TrailingLevelRegex().Match(classLevel);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedLevel))
            {
                resolvedLevel = Math.Max(1, parsedLevel);
            }

            foreach (var (className, sides) in ClassHitDieSides)
            {
                if (classLevel.Contains(className, StringComparison.OrdinalIgnoreCase))
                {
                    dieSides = sides;
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryResolve(string? classLevel, string? hitDie, int? level, out int resolvedLevel, out int dieSides) =>
        TryResolve(classLevel, hitDie, level, structured: null, out resolvedLevel, out dieSides);

    public static bool TryParseDie(string hitDie, out int dieSides)
    {
        dieSides = 0;
        var normalized = hitDie.Trim().ToLowerInvariant();
        if (normalized.StartsWith('d') && int.TryParse(normalized[1..], out dieSides))
        {
            return dieSides is >= 4 and <= 20;
        }

        return false;
    }

    public static int AverageDieRoll(int dieSides) => dieSides / 2 + 1;

    public static int ProficiencyBonus(int level) => 2 + (Math.Max(1, level) - 1) / 4;
}