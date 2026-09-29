using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Resolves structured class/level data from character fields for resource pool derivation.
/// </summary>
public static partial class CharacterClassResolver
{
    [GeneratedRegex(@"(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLevelRegex();

    public static IReadOnlyList<ClassLevelEntry> ResolveClassLevels(Character character)
    {
        if (character.SystemStats is Dnd5eExtension dnd && dnd.ClassLevels.Count > 0)
        {
            return dnd.ClassLevels;
        }

        var parsed = ParseClassLevelString(character.ClassLevel);
        if (parsed.Count == 0)
        {
            // "Human Wizard" with systemStats.level = 3: the level lives on the stats, not in the string.
            var statsLevel = character.SystemStats switch
            {
                Dnd5eExtension d => d.Level,
                Pf2eExtension p => p.Level,
                _ => null,
            };
            var text = character.ClassLevel?.Trim();
            if (statsLevel is > 0 && !string.IsNullOrEmpty(text) && !text.Contains('/'))
            {
                return [new ClassLevelEntry { Class = text, Level = statsLevel.Value }];
            }
        }

        return parsed;
    }

    public static bool HasClass(IReadOnlyList<ClassLevelEntry> classLevels, string classSlug) =>
        classLevels.Any(entry => ClassMatches(entry.Class, classSlug));

    public static int GetClassLevel(IReadOnlyList<ClassLevelEntry> classLevels, string classSlug) =>
        classLevels
            .Where(entry => ClassMatches(entry.Class, classSlug))
            .Sum(entry => entry.Level);

    private static bool ClassMatches(string className, string classSlug) =>
        className.Contains(classSlug, StringComparison.OrdinalIgnoreCase);

    private static List<ClassLevelEntry> ParseClassLevelString(string? classLevel)
    {
        if (string.IsNullOrWhiteSpace(classLevel))
        {
            return [];
        }

        var entries = new List<ClassLevelEntry>();
        foreach (var segment in classLevel.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = TrailingLevelRegex().Match(segment);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var level))
            {
                continue;
            }

            var className = segment[..match.Groups[1].Index].Trim();
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            entries.Add(new ClassLevelEntry { Class = className, Level = level });
        }

        return entries;
    }
}