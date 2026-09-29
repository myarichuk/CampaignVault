using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>One suspected half-wired aspect of a character (e.g. class declared, but its pool never created).</summary>
public sealed record WiringFinding(string Code, string Message);

/// <summary>
/// Detects characters (PCs and NPCs alike) whose class/feat/race declarations and derived mechanics disagree:
/// a Fighter with no action_surge pool, a class string that resolves to nothing, a stale proficiency bonus,
/// a caster with no spell DC, feats that don't exist in the ruleset data. Expected pools come from the real
/// <see cref="ResourcePoolInitializer"/> (diffed against what is stored) so gating rules are never duplicated.
/// Characters that declare no class (stat-block creatures) are only checked for unresolved references.
/// </summary>
public sealed class CharacterWiringAuditor(
    ResourcePoolInitializer pools,
    ClassDefinitionProvider classes,
    FeatDefinitionProvider? feats = null,
    BackgroundDefinitionProvider? backgrounds = null,
    RaceDefinitionProvider? races = null,
    ProgressionDefinitionProvider? progressions = null)
{
    private static readonly string[] Pf2eSeededSkills =
    [
        "Acrobatics", "Arcana", "Athletics", "Crafting", "Deception", "Diplomacy",
        "Intimidation", "Medicine", "Nature", "Occultism", "Performance", "Religion",
        "Society", "Stealth", "Survival", "Thievery", "Lore",
    ];

    /// <param name="catalog">
    /// SRD plus homebrew feats by normalized name (see <see cref="FeatEffectRules.CatalogAsync"/>). Without it only SRD feat data
    /// is consulted, so a homebrew feat would read as unknown; callers that can reach the database should pass it.
    /// </param>
    public IReadOnlyList<WiringFinding> Audit(Character character, string system, CampaignConfig? config) =>
        Audit(character, system, config, null);

    internal IReadOnlyList<WiringFinding> Audit(
        Character character, string system, CampaignConfig? config,
        IReadOnlyDictionary<string, FeatEffectRules.CatalogFeat>? catalog)
    {
        var findings = new List<WiringFinding>();
        if (character.SystemStats is null || system is not (RulesetSystem.Dnd5e or RulesetSystem.Pathfinder2e))
        {
            return findings;
        }

        var declared = CharacterClassResolver.ResolveClassLevels(character);
        if (declared.Count > 0)
        {
            AuditClasses(character, system, config, declared, findings);
        }
        else if (!string.IsNullOrWhiteSpace(character.ClassLevel)
                 && classes.TryResolveClass(system, character.ClassLevel, out _))
        {
            findings.Add(new WiringFinding("class_level_unparsed",
                $"classLevel '{character.ClassLevel}' names a class but no level could be read, so no class pools, proficiency or "
                + "caster data are derived. Write it as e.g. 'Fighter 4 / Rogue 3' or set systemStats.level."));
        }

        AuditReferences(character.SystemStats, system, findings, catalog);
        return findings;
    }

    /// <summary>Audit with the database-backed feat catalog, so homebrew feats are recognised and checked for declared effects.</summary>
    public async Task<IReadOnlyList<WiringFinding>> AuditAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session, string? campaignName,
        Character character, string system, CampaignConfig? config)
    {
        var catalog = feats is null || character.SystemStats is null
            ? null
            : await FeatEffectRules.CatalogAsync(session, feats, system, campaignName);
        return Audit(character, system, config, catalog);
    }

    public static string Format(Character character, IReadOnlyList<WiringFinding> findings) =>
        $"{character.Name}: " + string.Join(" ", findings.Select(f => $"[{f.Code}] {f.Message}"));

    private void AuditClasses(
        Character character,
        string system,
        CampaignConfig? config,
        IReadOnlyList<ClassLevelEntry> declared,
        List<WiringFinding> findings)
    {
        var stats = character.SystemStats!;
        var resolvedDefs = new List<ClassDefinition>();

        foreach (var entry in declared)
        {
            if (classes.TryResolveClass(system, entry.Class, out var def))
            {
                resolvedDefs.Add(def);
            }
            else
            {
                findings.Add(new WiringFinding("unresolved_class",
                    $"class '{entry.Class}' matches no {system} class definition, so no class pools, saves or caster data are derived. "
                    + "Use a standard class name (subclass names alone don't resolve)."));
            }
        }

        var total = declared.Sum(e => e.Level);
        var recordedLevel = stats switch
        {
            Dnd5eExtension d => d.Level,
            Pf2eExtension p => p.Level,
            _ => null,
        };

        if (recordedLevel is int level && level != total)
        {
            findings.Add(new WiringFinding("level_mismatch",
                $"classes sum to level {total} but systemStats.level is {level}; HP, proficiency and pool sizes derive from different levels."));
        }
        else if (recordedLevel is null && stats is Pf2eExtension)
        {
            findings.Add(new WiringFinding("level_missing",
                "systemStats.level is unset, so PF2e proficiency, skill and save modifiers cannot be derived."));
        }

        AuditPools(character, system, config, declared, resolvedDefs, findings);

        if (character.IsPc || character.IsPartyCompanion)
        {
            AuditRequiredChoices(stats, system, declared, findings);
        }

        // Soft findings depend on player choices (skills, proficiencies) that NPCs legitimately never make.
        var isPartyMember = character.IsPc || character.IsPartyCompanion;
        if (stats is Dnd5eExtension dnd)
        {
            AuditDnd5e(dnd, total, resolvedDefs, isPartyMember, findings);
        }
        else if (stats is Pf2eExtension pf2e)
        {
            AuditPf2e(pf2e, resolvedDefs, isPartyMember, findings);
        }
    }

    /// <summary>
    /// Features keyed on a recorded choice (Archery, Dueling, subclass abilities) silently do nothing when the choice was
    /// never written down, so a required enum choice at or below the class level with no record is partial wiring.
    /// </summary>
    private void AuditRequiredChoices(
        SystemExtension stats, string system, IReadOnlyList<ClassLevelEntry> declared, List<WiringFinding> findings)
    {
        if (progressions is null)
        {
            return;
        }

        var missing = new List<string>();
        foreach (var entry in declared)
        {
            if (!progressions.TryGetProgression(system, entry.Class, out var progression))
            {
                continue;
            }

            foreach (var (level, def) in progression.Levels.Where(l => l.Key <= entry.Level).OrderBy(l => l.Key))
            {
                foreach (var choice in def.Choices.Where(c => c.Required && c.Type == ChoiceType.Enum))
                {
                    var recorded = stats.LevelUpChoices.Any(r =>
                        CombatFeatureRules.Norm(r.Key) == CombatFeatureRules.Norm(choice.Key));
                    if (!recorded)
                    {
                        missing.Add($"{progression.ClassName} {choice.Key} (level {level})");
                    }
                }
            }
        }

        if (missing.Count > 0)
        {
            findings.Add(new WiringFinding("missing_choices",
                $"required level-up choices not recorded: {string.Join(", ", missing.Distinct())}. Features that depend on them "
                + "(fighting styles, subclass abilities) are not applied. Record them with level_up choices or "
                + "systemStats.levelUpChoices [{level, key, value}]."));
        }
    }

    private void AuditPools(
        Character character,
        string system,
        CampaignConfig? config,
        IReadOnlyList<ClassLevelEntry> declared,
        List<ClassDefinition> defs,
        List<WiringFinding> findings)
    {
        var stored = character.SystemStats!.ResourcePools;
        var expected = pools.ComputeDesiredPools(character, system, config);

        // Independent of the initializer: a class that lists a pool the schemas don't define, or define only for
        // other classes, can never get it — the expected/stored diff below would agree with that silent omission.
        var schemas = pools.GetSchemas(system, config);
        foreach (var def in defs)
        {
            foreach (var poolName in def.Pools)
            {
                if (!schemas.TryGetValue(poolName, out var template))
                {
                    findings.Add(new WiringFinding("class_pool_undefined",
                        $"class '{def.Name}' lists pool '{poolName}' but the active pool schemas don't define it "
                        + "(campaign ResourcePoolSchemas override?)."));
                }
                else if (template.ApplicableClasses is { Count: > 0 } applicable
                         && !applicable.Any(slug => CharacterClassResolver.HasClass(declared, slug)))
                {
                    findings.Add(new WiringFinding("class_pool_inapplicable",
                        $"class '{def.Name}' lists pool '{poolName}' but its schema is restricted to [{string.Join(", ", applicable)}]."));
                }
            }
        }

        var missing = expected.Keys.Where(k => !stored.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            findings.Add(new WiringFinding("missing_pools",
                $"class/level implies pools [{string.Join(", ", missing)}] but none are stored, so the engine cannot spend them. "
                + "Re-commit systemStats (any patch) or level_up to re-derive."));
        }

        var stale = expected
            .Where(kv => stored.TryGetValue(kv.Key, out var have) && have.Max != kv.Value.Max)
            .Select(kv => $"{kv.Key} (stored max {stored[kv.Key].Max}, expected {kv.Value.Max})")
            .ToList();
        if (stale.Count > 0)
        {
            findings.Add(new WiringFinding("stale_pools", $"pool maxima disagree with class/level: {string.Join("; ", stale)}."));
        }
    }

    private void AuditDnd5e(
        Dnd5eExtension stats,
        int totalLevel,
        List<ClassDefinition> defs,
        bool isPartyMember,
        List<WiringFinding> findings)
    {
        var expectedProf = Dnd5eClassProfileResolver.ProficiencyBonus(stats.Level ?? totalLevel);
        if (!stats.Attributes.TryGetValue("proficiencyBonus", out var prof) || Math.Abs(prof - expectedProf) >= 0.01f)
        {
            findings.Add(new WiringFinding("proficiency_stale",
                $"attributes.proficiencyBonus is {(stats.Attributes.ContainsKey("proficiencyBonus") ? prof.ToString("0.##") : "missing")}, "
                + $"expected +{expectedProf} at level {stats.Level ?? totalLevel}; skill/save/attack math built on it will be off."));
        }

        var backgroundSkills = BackgroundSkills(stats);
        if (isPartyMember && stats.SkillModifiers.Keys.All(k => backgroundSkills.Contains(k)))
        {
            findings.Add(new WiringFinding("class_skills_unset",
                stats.SkillModifiers.Count == 0
                    ? "skillModifiers is empty; class skill proficiencies were never committed."
                    : "skillModifiers holds only background-derived skills; class-chosen skill proficiencies were never committed "
                      + "(passive Perception and checks will read as untrained)."));
        }

        // Multiclassing never grants saving-throw proficiencies; only the starting class does.
        var missingSaves = defs.Take(1).SelectMany(d => d.SavingThrows)
            .Where(save => !stats.SavingThrowModifiers.Keys.Any(k => string.Equals(k, save, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingSaves.Count > 0)
        {
            findings.Add(new WiringFinding("class_saves_unset",
                $"class saving-throw proficiencies [{string.Join(", ", missingSaves)}] are missing from savingThrowModifiers, "
                + "so those saves roll the bare ability modifier without proficiency."));
        }

        var isCaster = defs.Any(d => d.CasterType is { } ct && ct != CasterType.None);
        if (isCaster && (stats.SpellcastingAbility is null || stats.SpellSaveDc is null || stats.SpellAttackBonus is null))
        {
            findings.Add(new WiringFinding("caster_unwired",
                "spellcasting class without spellcastingAbility/spellSaveDc/spellAttackBonus; spell attacks and saves have no DC to use."));
        }
    }

    private static void AuditPf2e(Pf2eExtension stats, List<ClassDefinition> defs, bool isPartyMember, List<WiringFinding> findings)
    {
        var seeded = stats.SkillProficiencies.Count == Pf2eSeededSkills.Length
            && Pf2eSeededSkills.All(s => stats.SkillProficiencies.TryGetValue(s, out var rank) && rank == Pf2eProficiencyRank.Trained);
        if (seeded && isPartyMember)
        {
            findings.Add(new WiringFinding("default_proficiencies",
                "every skill is Trained, which is the engine's placeholder seed rather than this class's real proficiencies; "
                + "commit skillProficiencies/saveProficiencies from the class and background."));
        }

        var isCaster = defs.Any(d => d.CasterType is { } ct && ct != CasterType.None);
        if (isCaster && (stats.SpellcastingAbility is null || stats.SpellDc is null))
        {
            findings.Add(new WiringFinding("caster_unwired",
                "spellcasting class without spellcastingAbility/spellDc; spell attacks and saves have no DC to use."));
        }
    }

    private HashSet<string> BackgroundSkills(Dnd5eExtension stats)
    {
        if (backgrounds is not null
            && !string.IsNullOrWhiteSpace(stats.Background)
            && backgrounds.TryGet(RulesetSystem.Dnd5e, stats.Background, out var background)
            && background is not null)
        {
            return new HashSet<string>(background.SkillProficiencies, StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void AuditReferences(
        SystemExtension stats, string system, List<WiringFinding> findings,
        IReadOnlyDictionary<string, FeatEffectRules.CatalogFeat>? catalog)
    {
        var unresolved = new List<string>();

        IEnumerable<string> featNames = stats switch
        {
            Dnd5eExtension d => d.Feats,
            Pf2eExtension p => [.. p.AncestryFeats, .. p.ClassFeats, .. p.SkillFeats, .. p.GeneralFeats],
            _ => [],
        };

        foreach (var name in featNames)
        {
            FeatEffectRules.CatalogFeat? known = null;
            var found = catalog is not null
                ? catalog.TryGetValue(CombatFeatureRules.Norm(name), out known)
                : feats is null || feats.TryGet(system, name, out _);
            if (!found)
            {
                unresolved.Add($"feat '{name}'");
                continue;
            }

            if (known is null)
            {
                continue;
            }

            if (!FeatEffectRules.PluginAvailable(known.Requires))
            {
                findings.Add(new WiringFinding("feat_gated_off",
                    $"feat '{name}' requires plugin '{known.Requires!.Plugin}', which is not loaded, so its effects are inert."));
            }
            else if (known.Homebrew && known.Effects.Count == 0 && !known.Adjudicated && !known.HasOtherMechanics)
            {
                findings.Add(new WiringFinding("feat_unimplemented_effects",
                    $"homebrew feat '{name}' declares no effects, so the engine applies nothing for it. Give it effects (attackBonus, "
                    + "skillBonus, ...) via upsert_feat, or set adjudicated=true if the DM will apply it by judgment."));
            }
        }

        if (stats is Dnd5eExtension dnd)
        {
            if (races is not null && !string.IsNullOrWhiteSpace(dnd.Race) && !races.TryGet(system, dnd.Race, out _))
            {
                unresolved.Add($"race '{dnd.Race}'");
            }

            if (backgrounds is not null && !string.IsNullOrWhiteSpace(dnd.Background)
                && !backgrounds.TryGet(system, dnd.Background, out _))
            {
                unresolved.Add($"background '{dnd.Background}'");
            }
        }
        else if (stats is Pf2eExtension pf2
                 && backgrounds is not null && !string.IsNullOrWhiteSpace(pf2.Background)
                 && !backgrounds.TryGet(system, pf2.Background, out _))
        {
            unresolved.Add($"background '{pf2.Background}'");
        }

        if (unresolved.Count > 0)
        {
            findings.Add(new WiringFinding("unresolved_references",
                $"{string.Join(", ", unresolved)} not found in {system} ruleset data, so their mechanical effects are not applied."));
        }
    }
}
