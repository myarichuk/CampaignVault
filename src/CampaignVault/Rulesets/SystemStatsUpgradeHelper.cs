using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

/// <summary>
/// Upgrades legacy base SystemExtension instances to the correct derived type (Dnd5eExtension/Pf2eExtension)
/// and populates ruleset-specific properties like SkillModifiers on load-time for backward compatibility.
/// Used by both CampaignRepository (on explicit character fetch) and WorldChangeDispatcher (on pre-load).
/// </summary>
public static class SystemStatsUpgradeHelper
{
    public static async Task UpgradeCharacterSystemStatsAsync(
        IAsyncDocumentSession session,
        Dictionary<string, Character> characters,
        string campaignName,
        CampaignDocumentKeys keys,
        ClassDefinitionProvider? classProvider = null,
        BackgroundDefinitionProvider? backgroundProvider = null)
    {
        if (characters.Count == 0)
        {
            return;
        }

        var config = await session.LoadAsync<CampaignConfig>(keys.Config(campaignName));
        var activeSystem = config?.ActiveSystem ?? RulesetSystem.Dnd5e;

        foreach (var character in characters.Values)
        {
            await UpgradeSystemStatsIfNeededAsync(session, character, activeSystem, classProvider, backgroundProvider, keys, campaignName);
        }
    }

    public static async Task UpgradeSystemStatsIfNeededAsync(
        IAsyncDocumentSession session,
        Character character,
        string activeSystem,
        ClassDefinitionProvider? classProvider = null,
        BackgroundDefinitionProvider? backgroundProvider = null,
        CampaignDocumentKeys? keys = null,
        string? campaignName = null)
    {
        if (character?.SystemStats == null)
        {
            return;
        }

        var statsType = character.SystemStats.GetType();

        var expectedType = activeSystem switch
        {
            RulesetSystem.Dnd5e => typeof(Dnd5eExtension),
            RulesetSystem.Pathfinder2e => typeof(Pf2eExtension),
            _ => typeof(SystemExtension)
        };

        // Only coerce the type if it's currently the base SystemExtension (legacy document).
        // If already the correct derived type, use it as-is.
        if (statsType == typeof(SystemExtension) && expectedType != typeof(SystemExtension))
        {
            character.SystemStats = SystemStatsMerger.CoerceToRuleset(character.SystemStats, activeSystem);
        }
        else if (statsType != typeof(SystemExtension) && statsType != expectedType)
        {
            // Already a derived type, but wrong one for this ruleset — don't auto-fix, just return.
            return;
        }

        // Derive ruleset-specific fields (SkillModifiers, proficiencies, etc.) even if the type was
        // already correct. Both methods internally no-op if their respective dicts are already populated.
        if (activeSystem == RulesetSystem.Dnd5e && character.SystemStats is Dnd5eExtension dnd5e)
        {
            DeriveD5eProficienciesIfEmpty(character, dnd5e, backgroundProvider);
        }
        else if (activeSystem == RulesetSystem.Pathfinder2e && character.SystemStats is Pf2eExtension pf2e)
        {
            DerivePf2eProficienciesIfEmpty(character, pf2e, backgroundProvider);
        }
    }

    private static void DeriveD5eProficienciesIfEmpty(
        Character character,
        Dnd5eExtension stats,
        BackgroundDefinitionProvider? backgroundProvider)
    {
        if (stats.SkillModifiers.Count > 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(stats.Background) && backgroundProvider != null)
        {
            if (backgroundProvider.TryGet(RulesetSystem.Dnd5e, stats.Background, out var background) && background != null)
            {
                if (!Dnd5eClassProfileResolver.TryResolve(
                        character.ClassLevel,
                        stats.HitDie,
                        stats.Level,
                        stats.ClassLevels,
                        out var level,
                        out _))
                {
                    level = stats.Level ?? 1;
                }

                var prof = level >= 1 ? Dnd5eClassProfileResolver.ProficiencyBonus(level) : 2;

                foreach (var skill in background.SkillProficiencies)
                {
                    if (!Dnd5eSkillTable.GoverningAbility.TryGetValue(skill, out var ability))
                    {
                        continue;
                    }

                    var abilityScore = GetAbilityScore(stats, ability);
                    stats.SkillModifiers[skill] = stats.GetAbilityModifier(abilityScore) + prof;
                }
            }
        }
    }

    private static void DerivePf2eProficienciesIfEmpty(
        Character character,
        Pf2eExtension stats,
        BackgroundDefinitionProvider? backgroundProvider)
    {
        if (stats.SkillModifiers.Count > 0)
        {
            return;
        }

        // Derive level the same way Pf2eDeriveProficiencyStep does: from ClassLevels, or fall back to Level.
        var level = stats.Level ?? 1;

        // Derive from SkillProficiencies rank if populated; otherwise from background.
        if (stats.SkillProficiencies.Count > 0)
        {
            Pf2eDeriveProficiencyStep.DeriveNumericModifiers(stats, level, stats.SkillProficiencies, stats.SkillModifiers, Pf2eSkillTable.KeyAbility);
            return;
        }

        // Fall back to background proficiencies if no explicit skill proficiencies are set yet.
        if (backgroundProvider != null && !string.IsNullOrWhiteSpace(stats.Background) && backgroundProvider.TryGet(RulesetSystem.Pathfinder2e, stats.Background, out var background) && background != null)
        {
            // Initialize SkillProficiencies from background's granted proficiencies, then derive modifiers.
            var backgroundSkills = new Dictionary<string, Pf2eProficiencyRank>();
            foreach (var skillName in background.SkillProficiencies)
            {
                backgroundSkills[skillName] = Pf2eProficiencyRank.Trained;
            }
            Pf2eDeriveProficiencyStep.DeriveNumericModifiers(stats, level, backgroundSkills, stats.SkillModifiers, Pf2eSkillTable.KeyAbility);
        }
    }

    private static int GetAbilityScore(Dnd5eExtension stats, string ability) => ability.ToLowerInvariant() switch
    {
        "strength" => stats.Strength,
        "dexterity" => stats.Dexterity,
        "constitution" => stats.Constitution,
        "intelligence" => stats.Intelligence,
        "wisdom" => stats.Wisdom,
        "charisma" => stats.Charisma,
        _ => 10,
    };
}
