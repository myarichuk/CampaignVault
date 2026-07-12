using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

internal static partial class BootstrapClassLevelHelper
{
    [GeneratedRegex(@"(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLevelRegex();

    public static void SyncClassLevelFromStats(Character character, string? classGained = null, int levelsGained = 1)
    {
        var level = character.SystemStats switch
        {
            Dnd5eExtension d => d.Level,
            Pf2eExtension p => p.Level,
            _ => null,
        };

        if (level is null or < 1 || string.IsNullOrWhiteSpace(character.ClassLevel))
        {
            return;
        }

        if (character.SystemStats is Dnd5eExtension dnd && dnd.ClassLevels.Count > 0)
        {
            var entry = !string.IsNullOrWhiteSpace(classGained)
                ? dnd.ClassLevels.FirstOrDefault(e =>
                    e.Class.Contains(classGained, StringComparison.OrdinalIgnoreCase))
                : null;
            entry ??= dnd.ClassLevels[^1];
            entry.Level = Math.Max(1, entry.Level + Math.Max(1, levelsGained));
            dnd.Level = dnd.ClassLevels.Sum(e => e.Level);
            character.ClassLevel = string.Join(" / ",
                dnd.ClassLevels.Select(e => $"{e.Class} {e.Level}"));
            return;
        }

        var match = TrailingLevelRegex().Match(character.ClassLevel);
        if (match.Success)
        {
            character.ClassLevel = character.ClassLevel[..match.Groups[1].Index] + level.Value;
        }
    }
}