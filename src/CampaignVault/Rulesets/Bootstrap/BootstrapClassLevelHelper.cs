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

        if (character.SystemStats is Dnd5eExtension dnd)
        {
            // A character kept as a string ("Fighter 5") gets its structured entries now, so a second class can be added to
            // them; without that the trailing number would be bumped instead. (The level-up handler does this before the
            // level is counted; this covers a caller that doesn't.)
            var gain = ClassGainedResolver.Resolve(character, RulesetSystem.Dnd5e, classGained);
            if (dnd.ClassLevels.Count == 0 && gain.IsNew)
            {
                ClassGainedResolver.EnsureStructuredEntries(character);
            }
            else if (dnd.ClassLevels.Count == 0 && Dnd5eClassProfileResolver.ParseClassLevels(character.ClassLevel) is { Count: > 1 } parsed)
            {
                dnd.ClassLevels = [.. parsed.Select(e => new ClassLevelEntry { Class = e.Class, Level = e.Level })];
            }

            if (dnd.ClassLevels.Count > 0)
            {
                var entry = gain.ClassName is null ? null : dnd.ClassLevels.FirstOrDefault(e => ClassGainedResolver.SameClass(e.Class, gain.ClassName));
                if (entry is null && gain is { IsNew: true, ClassName: not null })
                {
                    entry = new ClassLevelEntry { Class = gain.ClassName, Level = 0 };
                    dnd.ClassLevels.Add(entry);
                }

                entry ??= dnd.ClassLevels[^1];
                entry.Level = Math.Max(0, entry.Level) + Math.Max(1, levelsGained);
                dnd.Level = dnd.ClassLevels.Sum(e => e.Level);
                character.ClassLevel = string.Join(" / ", dnd.ClassLevels.Select(e => $"{e.Class} {e.Level}"));
                return;
            }
        }

        var match = TrailingLevelRegex().Match(character.ClassLevel);
        if (match.Success)
        {
            character.ClassLevel = character.ClassLevel[..match.Groups[1].Index] + level.Value;
        }
    }
}