using System.Text.RegularExpressions;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

internal static partial class BootstrapClassLevelHelper
{
    [GeneratedRegex(@"(\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLevelRegex();

    public static void SyncClassLevelFromStats(Character character)
    {
        var level = character.SystemStats switch
        {
            Dnd5eExtension d => d.Level,
            Pf2eExtension p => p.Level,
            Fallout2d20Extension f => f.Level,
            _ => null,
        };

        if (level is null or < 1 || string.IsNullOrWhiteSpace(character.ClassLevel))
        {
            return;
        }

        var match = TrailingLevelRegex().Match(character.ClassLevel);
        if (match.Success)
        {
            character.ClassLevel = character.ClassLevel[..match.Groups[1].Index] + level.Value;
        }
    }
}