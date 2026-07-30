using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Provides XP threshold calculations for different progression types (Standard, Slow, Fast).
/// Based on D&D 5e PHB and Pathfinder 2e standard tables.
/// </summary>
public static class XpThresholdCalculator
{
    // D&D 5e Standard XP thresholds (cumulative XP to reach each level)
    private static readonly IReadOnlyDictionary<int, int> Dnd5eStandardXp = new Dictionary<int, int>
    {
        [1] = 0,
        [2] = 300,
        [3] = 900,
        [4] = 2700,
        [5] = 6500,
        [6] = 14000,
        [7] = 23000,
        [8] = 34000,
        [9] = 48000,
        [10] = 64000,
        [11] = 85000,
        [12] = 100000,
        [13] = 120000,
        [14] = 140000,
        [15] = 165000,
        [16] = 195000,
        [17] = 225000,
        [18] = 265000,
        [19] = 305000,
        [20] = 355000
    };

    // PF2e uses a different XP system (800 XP per level by default, but 1000 is standard)
    private static readonly IReadOnlyDictionary<int, int> Pf2eStandardXp = new Dictionary<int, int>
    {
        [1] = 0,
        [2] = 1000,
        [3] = 2000,
        [4] = 3000,
        [5] = 4000,
        [6] = 5000,
        [7] = 6000,
        [8] = 7000,
        [9] = 8000,
        [10] = 9000,
        [11] = 10000,
        [12] = 11000,
        [13] = 12000,
        [14] = 13000,
        [15] = 14000,
        [16] = 15000,
        [17] = 16000,
        [18] = 17000,
        [19] = 18000,
        [20] = 19000
    };

    /// <summary>
    /// Gets the XP required to reach the specified level for the given system and progression type.
    /// </summary>
    public static int GetXpForLevel(RulesetSystem system, int level, XpProgressionType progression = XpProgressionType.Standard, Dictionary<int, int>? customThresholds = null)
    {
        if (customThresholds?.TryGetValue(level, out var customXp) == true)
        {
            return customXp;
        }

        var table = system switch
        {
            RulesetSystem.Dnd5e => Dnd5eStandardXp,
            RulesetSystem.Pathfinder2e => Pf2eStandardXp,
            _ => Dnd5eStandardXp // Default to 5e for Narrative/other systems
        };

        if (!table.TryGetValue(level, out var baseXp))
        {
            // Beyond level 20, extrapolate
            baseXp = table[20] + (level - 20) * (table[20] - table[19]);
        }

        return progression switch
        {
            XpProgressionType.Slow => baseXp * 2,
            XpProgressionType.Fast => baseXp / 2,
            XpProgressionType.Milestone => int.MaxValue, // No XP threshold for milestone
            _ => baseXp
        };
    }

    /// <summary>
    /// Gets the current level based on XP for the given system and progression type.
    /// </summary>
    public static int GetLevelFromXp(RulesetSystem system, int xp, XpProgressionType progression = XpProgressionType.Standard, Dictionary<int, int>? customThresholds = null)
    {
        if (progression == XpProgressionType.Milestone)
        {
            return 1; // Level determined narratively
        }

        for (var level = 20; level >= 1; level--)
        {
            if (xp >= GetXpForLevel(system, level, progression, customThresholds))
            {
                return level;
            }
        }
        return 1;
    }

    /// <summary>
    /// Gets the XP required to reach the next level from the current level.
    /// </summary>
    public static int GetXpToNextLevel(RulesetSystem system, int currentLevel, XpProgressionType progression = XpProgressionType.Standard, Dictionary<int, int>? customThresholds = null)
    {
        var nextLevel = Math.Min(currentLevel + 1, 20);
        var currentXp = GetXpForLevel(system, currentLevel, progression, customThresholds);
        var nextXp = GetXpForLevel(system, nextLevel, progression, customThresholds);
        return nextXp - currentXp;
    }

    /// <summary>
    /// Checks if a character has enough XP to level up.
    /// </summary>
    public static bool CanLevelUp(RulesetSystem system, int currentLevel, int xp, XpProgressionType progression = XpProgressionType.Standard, Dictionary<int, int>? customThresholds = null)
    {
        if (progression == XpProgressionType.Milestone)
        {
            return false; // Milestone doesn't use XP thresholds
        }

        if (currentLevel >= 20)
        {
            return false; // Max level
        }

        var nextLevelXp = GetXpForLevel(system, currentLevel + 1, progression, customThresholds);
        return xp >= nextLevelXp;
    }

    /// <summary>
    /// Gets the character's current level from their ClassLevel string or SystemStats.
    /// </summary>
    public static int GetCurrentLevel(Character character)
    {
        // Try to parse from ClassLevel string (e.g., "Fighter 5" or "Fighter 3/Wizard 2")
        if (!string.IsNullOrWhiteSpace(character.ClassLevel))
        {
            var parts = character.ClassLevel.Split('/');
            var total = 0;
            foreach (var part in parts)
            {
                var words = part.Trim().Split(' ');
                if (words.Length > 0 && int.TryParse(words[^1], out var lvl))
                {
                    total += lvl;
                }
            }
            if (total > 0)
            {
                return total;
            }
        }

        // Fall back to system stats
        if (character.SystemStats is Dnd5eExtension dnd5e && dnd5e.Level.HasValue)
        {
            return dnd5e.Level.Value;
        }

        if (character.SystemStats is Pf2eExtension pf2e && pf2e.Level.HasValue)
        {
            return pf2e.Level.Value;
        }

        return 1;
    }
}