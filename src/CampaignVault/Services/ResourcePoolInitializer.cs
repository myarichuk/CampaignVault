using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Initializes resource pools (spell slots, focus points, action points, etc.) on character creation.
/// Responsible for:
/// - Loading built-in defaults or campaign-specific schemas
/// - Deriving per-character max values from classLevels/level
/// - Populating Character.SystemExtension.ResourcePools
/// </summary>
public class ResourcePoolInitializer
{
    /// <summary>
    /// Initialize resource pools for a character based on system and config.
    /// </summary>
    /// <param name="character">Character to initialize (modifies in-place).</param>
    /// <param name="system">The active ruleset system.</param>
    /// <param name="campaignConfig">Campaign config (may contain custom ResourcePoolSchemas).</param>
    public void InitializePools(Character character, RulesetSystem system, CampaignConfig? campaignConfig)
    {
        if (character?.SystemStats == null)
        {
            return;
        }

        // Get pool schemas: campaign-specific or built-in defaults
        var schemas = campaignConfig?.ResourcePoolSchemas?.Count > 0
            ? campaignConfig.ResourcePoolSchemas
            : ResourcePoolDefaults.GetDefaults(system);

        character.SystemStats.ResourcePools ??= [];

        // Derive character level from systemStats or classLevel
        var charLevel = DeriveCharacterLevel(character);

        foreach (var (poolName, template) in schemas)
        {
            // Skip if this pool doesn't apply to this system
            if (template.ApplicableSystems != null && !template.ApplicableSystems.Contains(system.ToString().ToLower()))
            {
                continue;
            }

            // Derive max value based on level
            var maxValue = DeriveMaxValue(template, charLevel);

            // Initialize pool at full capacity
            character.SystemStats.ResourcePools[poolName] = new ResourcePool
            {
                Current = maxValue,
                Max = maxValue,
                Recovery = template.Recovery,
                LastRecoveredDay = 0 // Just created, recovered at day 0
            };
        }
    }

    /// <summary>Derive character level from systemStats or classLevel string.</summary>
    private int DeriveCharacterLevel(Character character)
    {
        if (character.SystemStats is Dnd5eExtension dnd5e && dnd5e.Level.HasValue)
        {
            return dnd5e.Level.Value;
        }

        if (character.SystemStats is Pf2eExtension pf2e && pf2e.Level.HasValue)
        {
            return pf2e.Level.Value;
        }

        if (character.SystemStats is Fallout2d20Extension fallout && fallout.Level.HasValue)
        {
            return fallout.Level.Value;
        }

        // Try parsing from classLevel string (e.g., "Fighter 5")
        if (!string.IsNullOrWhiteSpace(character.ClassLevel))
        {
            var parts = character.ClassLevel.Split(' ');
            if (parts.Length > 1 && int.TryParse(parts[^1], out var level))
            {
                return level;
            }
        }

        return 1; // Default
    }

    /// <summary>Derive max value for a pool based on character level and template config.</summary>
    private int DeriveMaxValue(ResourcePoolTemplate template, int charLevel)
    {
        // If there's a level-to-max mapping, use it
        if (template.LevelToMaxMap?.Count > 0)
        {
            // Find the highest level that doesn't exceed charLevel
            var applicableLevels = template.LevelToMaxMap.Keys
                .Where(k => int.TryParse(k, out var lvl) && lvl <= charLevel)
                .Select(k => int.Parse(k))
                .OrderByDescending(l => l)
                .ToList();

            if (applicableLevels.Count > 0)
            {
                var selectedLevel = applicableLevels.First();
                if (template.LevelToMaxMap.TryGetValue(selectedLevel.ToString(), out var max))
                {
                    return max;
                }
            }
        }

        // Fall back to default max
        return template.DefaultMax;
    }
}
