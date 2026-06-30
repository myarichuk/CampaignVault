using CampaignVault.Models;

namespace CampaignVault.Services;

/// <summary>
/// Initializes and syncs resource pools (spell slots, focus points, action points, etc.).
/// Preserves spent resources on re-sync; removes pools that no longer apply to the character's classes.
/// </summary>
public class ResourcePoolInitializer
{
    private readonly ResourcePoolProvider? _provider;
    private readonly ClassDefinitionProvider? _classProvider;

    public ResourcePoolInitializer(ResourcePoolProvider? provider = null, ClassDefinitionProvider? classProvider = null)
    {
        _provider = provider;
        _classProvider = classProvider;
    }

    public void InitializePools(Character character, RulesetSystem system, CampaignConfig? campaignConfig)
    {
        if (character?.SystemStats == null)
        {
            return;
        }

        IReadOnlyDictionary<string, ResourcePoolTemplate> schemas;
        if (campaignConfig?.ResourcePoolSchemas?.Count > 0)
        {
            schemas = campaignConfig.ResourcePoolSchemas;
        }
        else if (_provider != null)
        {
            schemas = _provider.GetPoolsForSystem(system);
        }
        else
        {
#pragma warning disable CS0618
            schemas = ResourcePoolDefaults.GetDefaults(system);
#pragma warning restore CS0618
        }

        character.SystemStats.ResourcePools ??= [];

        var classLevels = CharacterClassResolver.ResolveClassLevels(character);
        var characterLevel = DeriveCharacterLevel(character);
        var casterLevel = system == RulesetSystem.Dnd5e
            ? Dnd5eCasterLevelHelper.ComputeCasterLevel(classLevels, _classProvider)
            : 0;

        var desiredPools = new Dictionary<string, ResourcePool>();

        foreach (var (poolName, template) in schemas)
        {
            if (template.ApplicableSystems != null && !template.ApplicableSystems.Contains(system.ToSlug()))
            {
                continue;
            }

            if (!TryResolveLevelForPool(poolName, template, system, classLevels, characterLevel, casterLevel,
                    _classProvider, out var levelForMax))
            {
                continue;
            }

            var maxValue = DeriveMaxValue(template, levelForMax);
            if (maxValue <= 0)
            {
                continue;
            }

            desiredPools[poolName] = BuildPool(template, maxValue,
                character.SystemStats.ResourcePools.GetValueOrDefault(poolName));
        }

        character.SystemStats.ResourcePools = desiredPools;
    }

    private static bool TryResolveLevelForPool(
        string poolName,
        ResourcePoolTemplate template,
        RulesetSystem system,
        IReadOnlyList<ClassLevelEntry> classLevels,
        int characterLevel,
        int casterLevel,
        ClassDefinitionProvider? classProvider,
        out int levelForMax)
    {
        if (IsSpellSlotPool(poolName))
        {
            if (system == RulesetSystem.Dnd5e)
            {
                if (casterLevel <= 0)
                {
                    levelForMax = 0;
                    return false;
                }

                levelForMax = casterLevel;
                return true;
            }

            if (system == RulesetSystem.Pathfinder2e)
            {
                if (!Pf2eCasterClasses.HasCaster(classLevels, classProvider))
                {
                    levelForMax = 0;
                    return false;
                }

                levelForMax = characterLevel;
                return true;
            }

            levelForMax = 0;
            return false;
        }

        if (template.ApplicableClasses is { Count: > 0 })
        {
            var matchingClasses = template.ApplicableClasses
                .Where(slug => CharacterClassResolver.HasClass(classLevels, slug))
                .ToList();

            if (matchingClasses.Count == 0)
            {
                levelForMax = 0;
                return false;
            }

            levelForMax = matchingClasses
                .Max(slug => CharacterClassResolver.GetClassLevel(classLevels, slug));
            return true;
        }

        levelForMax = characterLevel;
        return true;
    }

    private static ResourcePool BuildPool(ResourcePoolTemplate template, int maxValue, ResourcePool? existing)
    {
        var recovery = template.Recovery ?? RecoveryType.LongRest;

        if (existing == null)
        {
            return new ResourcePool
            {
                Current = maxValue,
                Max = maxValue,
                Recovery = recovery,
                LastRecoveredDay = 0
            };
        }

        return existing with
        {
            Max = maxValue,
            Recovery = recovery,
            Current = Math.Min(existing.Current, maxValue)
        };
    }

    private static bool IsSpellSlotPool(string poolName) =>
        poolName.StartsWith("spell_slots_", StringComparison.Ordinal);

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

        var classLevels = CharacterClassResolver.ResolveClassLevels(character);
        if (classLevels.Count > 0)
        {
            return classLevels.Sum(e => e.Level);
        }

        return 1;
    }

    private static int DeriveMaxValue(ResourcePoolTemplate template, int level)
    {
        if (template.LevelToMaxMap?.Count > 0)
        {
            var applicableLevels = template.LevelToMaxMap.Keys
                .Where(k => int.TryParse(k, out var lvl) && lvl <= level)
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

            return 0;
        }

        return template.DefaultMax ?? 0;
    }
}