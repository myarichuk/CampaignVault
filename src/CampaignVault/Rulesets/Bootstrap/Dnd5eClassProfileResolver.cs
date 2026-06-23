using System.Text.RegularExpressions;

namespace CampaignVault.Rulesets.Bootstrap;

internal static partial class Dnd5eClassProfileResolver
{
    [GeneratedRegex(@"(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLevelRegex();

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

    public static bool TryResolve(string? classLevel, string? hitDie, int? level, out int resolvedLevel, out int dieSides)
    {
        resolvedLevel = level ?? 1;
        dieSides = 0;

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