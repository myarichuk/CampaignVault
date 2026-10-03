using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Services;

/// <summary>
/// Initializes and syncs resource pools (spell slots, focus points, action points, etc.).
/// Preserves spent resources on re-sync; removes pools that no longer apply to the character's classes.
/// </summary>
public class ResourcePoolInitializer : IRulesetDataInitializer
{
    private readonly ResourcePoolProvider? _provider;
    private readonly ClassDefinitionProvider? _classProvider;
    private readonly FeatDefinitionProvider? _featProvider;
    private readonly ProgressionDefinitionProvider? _progressionProvider;

    public ResourcePoolInitializer(
        ResourcePoolProvider? provider = null,
        ClassDefinitionProvider? classProvider = null,
        FeatDefinitionProvider? featProvider = null,
        ProgressionDefinitionProvider? progressionProvider = null)
    {
        _provider = provider;
        _classProvider = classProvider;
        _featProvider = featProvider;
        _progressionProvider = progressionProvider;
    }

    public void InitializePools(Character? character, string system, CampaignConfig? campaignConfig)
    {
        if (character?.SystemStats == null)
        {
            return;
        }

        character.SystemStats.ResourcePools = ComputeDesiredPools(character, system, campaignConfig);
    }

    /// <summary>The pool schemas in force for a campaign: its own override when present, else the system's YAML.</summary>
    public IReadOnlyDictionary<string, ResourcePoolTemplate> GetSchemas(string system, CampaignConfig? campaignConfig)
    {
        if (campaignConfig?.ResourcePoolSchemas?.Count > 0)
        {
            return campaignConfig.ResourcePoolSchemas;
        }

        if (_provider != null)
        {
            return _provider.GetPoolsForSystem(system);
        }

        throw new InvalidOperationException(
            "ResourcePoolProvider is required when campaign config has no ResourcePoolSchemas. " +
            "Register ResourcePoolInitializer through DI (CampaignVaultModule) or supply schemas in CampaignConfig.");
    }

    /// <summary>
    /// The pools this character should have right now, given its classes, level and feats. Pure with
    /// respect to the character: preserves spent amounts from the stored pools but assigns nothing, so
    /// audits can diff "expected" against "stored" without running the mutating initializer.
    /// </summary>
    public Dictionary<string, ResourcePool> ComputeDesiredPools(Character character, string system, CampaignConfig? campaignConfig)
    {
        var desiredPools = new Dictionary<string, ResourcePool>();
        if (character.SystemStats == null)
        {
            return desiredPools;
        }

        var schemas = GetSchemas(system, campaignConfig);

        var existingPools = character.SystemStats.ResourcePools ?? [];

        var classLevels = CharacterClassResolver.ResolveClassLevels(character);
        var characterLevel = DeriveCharacterLevel(character);
        var casterLevel = system == RulesetSystem.Dnd5e
            ? Dnd5eCasterLevelHelper.ComputeCasterLevel(classLevels, _classProvider,
                entry => CharacterClassFeatures.OptionSpellcastingFor(character, system, _progressionProvider, entry)?.CasterType)
            : 0;

        foreach (var (poolName, template) in schemas)
        {
            if (template.FeatGrantedOnly == true || template.GrantedOnly == true)
                continue;

            TryAddPool(
                character,
                poolName,
                template,
                system,
                classLevels,
                characterLevel,
                casterLevel,
                _classProvider,
                existingPools,
                desiredPools);
        }

        AddGrantedPools(
            character,
            system,
            schemas,
            classLevels,
            characterLevel,
            casterLevel,
            existingPools,
            desiredPools);

        // Pools the schemas don't know about (hand-added, or from a plugin that dropped its schema) can't be
        // re-derived, so rebuilding must not silently delete them. Schema pools that stopped applying still go.
        foreach (var (name, pool) in existingPools)
        {
            if (!schemas.ContainsKey(name))
            {
                desiredPools.TryAdd(name, pool);
            }
        }

        return desiredPools;
    }

    /// <summary>
    /// Pools granted by name: a feat's <c>extraPools</c> and the <c>pools</c> of the class features the character has (a
    /// picked subclass's). They go through the same class and level rules as any other pool.
    /// </summary>
    private void AddGrantedPools(
        Character character,
        string system,
        IReadOnlyDictionary<string, ResourcePoolTemplate> schemas,
        IReadOnlyList<ClassLevelEntry> classLevels,
        int characterLevel,
        int casterLevel,
        Dictionary<string, ResourcePool> existingPools,
        Dictionary<string, ResourcePool> desiredPools)
    {
        var granted = new List<string>();
        if (_featProvider != null)
        {
            foreach (var featName in CollectFeatNames(character.SystemStats, system))
            {
                if (_featProvider.TryGet(system, featName, out var feat))
                    granted.AddRange(feat.ExtraPools);
            }
        }

        granted.AddRange(CharacterClassFeatures.Features(character, system, _progressionProvider).SelectMany(f => f.Feature.Pools));

        foreach (var poolName in granted)
        {
            if (desiredPools.ContainsKey(poolName) || !schemas.TryGetValue(poolName, out var template))
                continue;

            // Route through the same class/caster-level gating as class-granted pools so a granted pool restricted to
            // a class, or scaled by caster level, behaves identically regardless of how it was granted.
            TryAddPool(
                character,
                poolName,
                template,
                system,
                classLevels,
                characterLevel,
                casterLevel,
                _classProvider,
                existingPools,
                desiredPools);
        }
    }

    private static bool TryAddPool(
        Character character,
        string poolName,
        ResourcePoolTemplate template,
        string system,
        IReadOnlyList<ClassLevelEntry> classLevels,
        int characterLevel,
        int casterLevel,
        ClassDefinitionProvider? classProvider,
        Dictionary<string, ResourcePool> existingPools,
        Dictionary<string, ResourcePool> desiredPools)
    {
        if (template.ApplicableSystems != null && !template.ApplicableSystems.Contains(system.ToSlug()))
            return false;

        if (!TryResolveLevelForPool(poolName, template, system, classLevels, characterLevel, casterLevel,
                classProvider, out var levelForMax))
            return false;

        var maxValue = DeriveMaxValue(template, levelForMax, character, system, characterLevel);
        var existing = existingPools.GetValueOrDefault(poolName);
        if (template.OwnerManaged == true)
        {
            // The owner sets Max/Current (e.g. a meter derived from other stats); create once, never rebuild.
            desiredPools[poolName] = existing ?? BuildPool(poolName, template, Math.Max(0, maxValue), null, levelForMax);
            return true;
        }

        if (maxValue <= 0)
            return false;

        var built = BuildPool(poolName, template, maxValue, existing, levelForMax);

        // A PC's or companion's purse is the player's to record, so it starts (and a legacy full one resets) empty. An
        // NPC with no purse record gets a modest one, so a robbed merchant or a searched guard has something on them.
        // An NPC who has since spent down to 0 keeps 0: only a missing pool or a reset legacy default is reseeded.
        if (poolName == "gold" && !character.IsPc && !character.IsPartyCompanion && built.Current == 0
            && (existing == null || existing.Current > 0))
        {
            built = built with { Current = Math.Min(NpcPurse(character.Id, characterLevel), built.Max) };
        }

        desiredPools[poolName] = built;
        return true;
    }

    /// <summary>A believable pocketful for an NPC, scaled by level (3-15 gp per level). Seeded by id, so the same
    /// character always rolls the same purse and re-deriving pools never reshuffles it.</summary>
    internal static int NpcPurse(string? characterId, int level)
    {
        var seed = 17;
        foreach (var ch in characterId ?? "")
        {
            seed = unchecked(seed * 31 + ch);
        }

        return new Random(seed).Next(3, 16) * Math.Max(1, level);
    }

    private static IReadOnlyList<string> CollectFeatNames(SystemExtension stats, string system) =>
        system switch
        {
            RulesetSystem.Dnd5e when stats is Dnd5eExtension dnd => dnd.Feats,
            RulesetSystem.Pathfinder2e when stats is Pf2eExtension pf2 =>
            [
                .. pf2.AncestryFeats,
                .. pf2.ClassFeats,
                .. pf2.SkillFeats,
                .. pf2.GeneralFeats
            ],
            _ => [],
        };

    private static bool TryResolveLevelForPool(
        string poolName,
        ResourcePoolTemplate template,
        string system,
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

    private static ResourcePool BuildPool(string poolName, ResourcePoolTemplate template, int maxValue, ResourcePool? existing, int level)
    {
        var recovery = TryAtLevel(template.RecoveryByLevel, level, out var leveledRecovery)
            ? leveledRecovery
            : template.Recovery ?? RecoveryType.LongRest;
        var die = TryAtLevel(template.DieByLevel, level, out var leveledDie) ? leveledDie : template.Die;
        string? startsAt;
        try
        {
            startsAt = ResourcePoolTemplate.NormalizeStartsAt(template.StartsAt);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"Invalid startsAt '{template.StartsAt}' for resource pool '{poolName}'. " +
                "Use 'zero' (a meter that fills up) or 'max' (the default), or omit it.", ex);
        }

        if (existing == null)
        {
            return new ResourcePool
            {
                Current = startsAt == "zero" ? 0 : maxValue,
                Max = maxValue,
                Recovery = recovery,
                LastRecoveredDay = 0,
                Die = die,
            };
        }

        // Characters created before a purse started empty carry the old default: full to the ceiling, on a pool that
        // never recovers, never touched. Nobody earns exactly the ceiling untouched, so it is the default, not wealth.
        var legacyFullPurse = startsAt == "zero" && recovery == RecoveryType.Never
                              && existing.Current == existing.Max && existing.Max == maxValue && maxValue > 0;

        return existing with
        {
            Max = maxValue,
            Recovery = recovery,
            Current = legacyFullPurse ? 0 : Math.Min(existing.Current, maxValue),
            Die = die,
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

        var classLevels = CharacterClassResolver.ResolveClassLevels(character);
        if (classLevels.Count > 0)
        {
            return classLevels.Sum(e => e.Level);
        }

        return 1;
    }

    /// <summary>The value of the highest level key at or below <paramref name="level"/>; false when none is reached.</summary>
    private static bool TryAtLevel<T>(Dictionary<string, T>? byLevel, int level, out T value)
    {
        value = default!;
        var reached = byLevel?
            .Where(kv => int.TryParse(kv.Key, out var l) && l <= level)
            .OrderByDescending(kv => int.Parse(kv.Key))
            .ToList();
        if (reached is not { Count: > 0 })
            return false;

        value = reached[0].Value;
        return true;
    }

    private static int DeriveMaxValue(ResourcePoolTemplate template, int level, Character character, string system, int characterLevel)
    {
        if (template.MaxFrom is { } formula)
        {
            var value = formula.LevelMultiplier * level + formula.Plus;
            if (!string.IsNullOrWhiteSpace(formula.Ability))
                value += AbilityModifier(character.SystemStats, formula.Ability);
            if (formula.ProficiencyBonus && system == RulesetSystem.Dnd5e)
                value += Dnd5eClassProfileResolver.ProficiencyBonus(Math.Max(1, characterLevel));
            return Math.Max(formula.Min, value);
        }

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

    private static int AbilityModifier(SystemExtension? stats, string ability) => stats switch
    {
        Dnd5eExtension dnd => dnd.GetAbilityModifier(ability.ToLowerInvariant() switch
        {
            "strength" => dnd.Strength,
            "dexterity" => dnd.Dexterity,
            "constitution" => dnd.Constitution,
            "intelligence" => dnd.Intelligence,
            "wisdom" => dnd.Wisdom,
            "charisma" => dnd.Charisma,
            _ => 10,
        }),
        Pf2eExtension pf2 => ability.ToLowerInvariant() switch
        {
            "strength" => pf2.StrengthMod,
            "dexterity" => pf2.DexterityMod,
            "constitution" => pf2.ConstitutionMod,
            "intelligence" => pf2.IntelligenceMod,
            "wisdom" => pf2.WisdomMod,
            "charisma" => pf2.CharismaMod,
            _ => 0,
        },
        _ => 0,
    };
}
