using CampaignVault.Rulesets.Creation;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

public class CharacterCreateHandler : IWorldChangeHandler
{
    private readonly CampaignDocumentKeys _keys;
    private readonly CharacterBootstrapOrchestrator _bootstrap;
    private readonly ResourcePoolInitializer _poolInitializer;
    private readonly ClassDefinitionProvider _classProvider;
    private readonly CharacterWiringAuditor? _auditor;

    public CharacterCreateHandler(
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap,
        ResourcePoolInitializer poolInitializer,
        ClassDefinitionProvider classProvider,
        CharacterWiringAuditor? auditor = null)
    {
        _auditor = auditor;
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _poolInitializer = poolInitializer ?? throw new ArgumentNullException(nameof(poolInitializer));
        _classProvider = classProvider ?? throw new ArgumentNullException(nameof(classProvider));
    }

    public bool ShouldHandle(WorldChange change) => change is CharacterCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var cc = (CharacterCreate)change;
        if (string.IsNullOrWhiteSpace(cc.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        var existing = await ctx.Session.LoadAsync<Character>(cc.CharacterId, ct);
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(ctx.CampaignName)
                && CampaignEntityVisibility.TryGetInvisibilityReason(existing, ctx.CampaignName, out var hidden))
            {
                return ChangeHandlerResult.Failure(hidden);
            }
            existing.Name = cc.Name ?? existing.Name;
            if (cc.Notes != null)
            {
                existing.Notes = cc.Notes;
            }

            if (cc.CurrentLocationId != null)
            {
                existing.CurrentLocationId = cc.CurrentLocationId;
                existing.DepartedAtDay = null;
                existing.DepartedFromLocationId = null;
            }

            if (cc.CurrentActivity != null)
            {
                existing.CurrentActivity = cc.CurrentActivity;
            }

            if (cc.KeepAlive)
            {
                existing.KeepAlive = cc.KeepAlive;
            }

            if (!LifeStageRules.TryChange(existing.LifeStage, cc.LifeStage, out var mergedStage, out var stageError))
            {
                return ChangeHandlerResult.Failure(stageError!);
            }

            existing.LifeStage = mergedStage;

            if (cc.IsPc || cc.IsPartyCompanion || existing.IsPc || existing.IsPartyCompanion)
            {
                var mergedIsPc = cc.IsPc || existing.IsPc;
                var mergedCompanion = cc.IsPartyCompanion || existing.IsPartyCompanion;
                if (!CharacterPartyRules.TryValidate(mergedIsPc, mergedCompanion, existing.CampaignName ?? ctx.CampaignName,
                        out var partyError))
                {
                    return ChangeHandlerResult.Failure(partyError!);
                }

                existing.IsPc = mergedIsPc;
                existing.IsPartyCompanion = mergedCompanion;
            }

            if (cc.Schedule != null)
            {
                existing.Schedule = cc.Schedule;
            }

            if (cc.Psychology != null)
            {
                existing.Psychology = cc.Psychology;
            }

            if (cc.MaxHp.HasValue)
            {
                existing.MaxHp = cc.MaxHp.Value;
            }

            if (cc.CurrentHp.HasValue)
            {
                existing.CurrentHp = Math.Clamp(cc.CurrentHp.Value, 0, existing.MaxHp);
            }

            if (cc.ClassLevel != null)
            {
                existing.ClassLevel = cc.ClassLevel;
            }

            if (cc.ControlledById != null || cc.MinionBinding != null)
            {
                var (linkError, controller) = await MinionLinkApplier.ResolveControllerAsync(
                    ctx, existing.Id, cc.ControlledById, cc.MinionBinding, ct);
                if (linkError != null)
                {
                    return ChangeHandlerResult.Failure(linkError);
                }

                if (controller != null)
                {
                    MinionLinkApplier.ApplyLink(existing, controller, cc.MinionBinding);
                }
            }

            if (cc.SystemStats != null)
            {
                var existingSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(ctx, _keys, ct);
                if (!SystemStatsMerger.TryValidateRuleset(cc.SystemStats, existingSystem,
                        out var existingValidationError))
                {
                    return ChangeHandlerResult.Failure(existingValidationError!);
                }

                existing.SystemStats = SystemStatsMerger.Merge(
                    existing.SystemStats ?? SystemStatsMerger.CreateDefault(existingSystem),
                    SystemStatsMerger.CoerceToRuleset(cc.SystemStats, existingSystem),
                    existingSystem);
            }

            var activeSystemForExisting =
                await CharacterHandlerHelpers.ResolveActiveSystemAsync(ctx, _keys, ct);
            // Upsert, not Create: the character already had its race/ancestry bonuses applied once.
            await ApplyBootstrapAsync(existing, activeSystemForExisting, cc.MaxHp, cc.CurrentHp, null,
                BootstrapTrigger.Upsert, ctx, ct);

            // Reinitialize resource pools if needed (in case level/class changed)
            var campaignConfigExisting = !string.IsNullOrEmpty(ctx.CampaignName)
                ? await ctx.Session.LoadAsync<CampaignConfig>(_keys.Config(ctx.CampaignName), ct)
                : null;
            _poolInitializer.InitializePools(existing, activeSystemForExisting, campaignConfigExisting);

            var hint = existing.KeepAlive
                ? " For existing PCs, prefer commit with activity/character_update instead of character_create. Call get_party to confirm PCs already exist."
                : string.Empty;
            ctx.RecordEntityCollision(cc.CharacterId,
                $"Warning: Character {cc.CharacterId} already exists. Updated existing character fields.{hint}");
            return ChangeHandlerResult.Ok;
        }

        var (createLinkError, createController) = await MinionLinkApplier.ResolveControllerAsync(
            ctx, cc.CharacterId, cc.ControlledById, cc.MinionBinding, ct);
        if (createLinkError != null)
        {
            return ChangeHandlerResult.Failure(createLinkError);
        }

        var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(ctx, _keys, ct);

        if (cc.SystemStats != null &&
            !SystemStatsMerger.TryValidateRuleset(cc.SystemStats, activeSystem, out var validationError))
        {
            return ChangeHandlerResult.Failure(validationError!);
        }

        var systemStats = SystemStatsMerger.CreateDefault(activeSystem);
        if (cc.SystemStats != null)
        {
            systemStats = SystemStatsMerger.Merge(
                systemStats,
                SystemStatsMerger.CoerceToRuleset(cc.SystemStats, activeSystem),
                activeSystem);
        }

        var newChar = new Character
        {
            Id = cc.CharacterId,
            Name = cc.Name ?? "Unnamed",
            Notes = cc.Notes,
            CurrentLocationId = cc.CurrentLocationId,
            CurrentActivity = cc.CurrentActivity,
            KeepAlive = cc.KeepAlive || cc.IsPc || cc.IsPartyCompanion,
            IsPc = cc.IsPc,
            IsPartyCompanion = cc.IsPartyCompanion,
            LifeStage = cc.LifeStage,
            Schedule = cc.Schedule,
            Psychology = cc.Psychology ?? new PsychologyProfile(),
            ClassLevel = cc.ClassLevel,
            MaxHp = cc.MaxHp ?? 0,
            CurrentHp = cc.CurrentHp ?? cc.MaxHp ?? 0,
            SystemStats = systemStats
        };

        if (createController != null)
        {
            MinionLinkApplier.ApplyLink(newChar, createController, cc.MinionBinding);
        }

        if (string.IsNullOrEmpty(newChar.CampaignName))
        {
            newChar.CampaignName = ctx.CampaignName;
        }

        if (!CharacterPartyRules.TryValidate(newChar.IsPc, newChar.IsPartyCompanion, newChar.CampaignName,
                out var createPartyError))
        {
            return ChangeHandlerResult.Failure(createPartyError!);
        }

        await ApplyBootstrapAsync(newChar, activeSystem, cc.MaxHp, cc.CurrentHp, null, BootstrapTrigger.Create, ctx, ct);

        // Initialize resource pools (spell slots, focus points, action points, etc.)
        var campaignConfig = !string.IsNullOrEmpty(ctx.CampaignName)
            ? await ctx.Session.LoadAsync<CampaignConfig>(_keys.Config(ctx.CampaignName), ct)
            : null;
        _poolInitializer.InitializePools(newChar, activeSystem, campaignConfig);

        RecordClassResolutionEcho(ctx, newChar, activeSystem, cc.ClassLevel);
        await RecordWiringFindingsAsync(ctx, _auditor, newChar, activeSystem, campaignConfig);

        await ctx.Session.StoreAsync(newChar, ct);
        ctx.RegisterNewCharacter(newChar);

        // The sweep drops NPCs with neither a schedule nor keepAlive once their location goes unvisited. Right for a
        // passer-by, a silent loss for anyone meant to be there: say so while the DM can still choose.
        if (!newChar.IsPc && !newChar.IsPartyCompanion && !newChar.KeepAlive && newChar.Schedule == null
            && !newChar.Id.StartsWith("chars/transient_encounter_", StringComparison.OrdinalIgnoreCase))
        {
            ((ChangeContext)ctx).RecordMessage(
                $"NOTE: {newChar.Name} ({newChar.Id}) has no schedule and keepAlive is false, so the engine will quietly " +
                "remove them once their location goes unvisited. If they should stay, set keepAlive: true (or give " +
                "them a schedule); if they are a passer-by, ignore this.");
        }

        return ChangeHandlerResult.Ok;
    }

    private void RecordClassResolutionEcho(
        IChangeContext context,
        Character character,
        string system,
        string? classLevelInput)
    {
        var ctx = (ChangeContext)context;
        if (string.IsNullOrWhiteSpace(classLevelInput))
            return;

        var classLevels = CharacterClassResolver.ResolveClassLevels(character);
        if (classLevels.Count == 0)
            return;

        // Emit a resolved summary for the first (primary) class
        var primary = classLevels[0];
        if (!_classProvider.TryResolveClass(system, primary.Class, out var classDef))
        {
            // Soft warning — unknown class, list known options
            var known = _classProvider.GetClassesForSystem(system);
            var knownNames = string.Join(", ", known.Values
                .SelectMany(d => d.Aliases)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            context.RecordMessage(
                $"[WARNING] Class '{primary.Class}' did not match any known {system} class definition. " +
                $"Known classes: {knownNames}. " +
                $"Character was created, but resource pools may be incomplete. " +
                $"Use get_system_handbook to see available classes.");
            return;
        }

        var poolsInitialized = character.SystemStats?.ResourcePools ?? new Dictionary<string, ResourcePool>();
        var poolSummary = string.Join(", ",
            poolsInitialized.Select(kvp => $"{kvp.Key}:{kvp.Value.Max}"));

        var casterType = classDef.CasterType ?? CasterType.None;
        context.RecordMessage(
            $"[RESOLVED] class={classDef.Name}, casterType={casterType}" +
            (poolsInitialized.Count > 0 ? $", pools=[{poolSummary}]" : ", pools=[]"));
    }

    private Task ApplyBootstrapAsync(
        Character character,
        string activeSystem,
        int? explicitMaxHp,
        int? explicitCurrentHp,
        HitPointDerivationMode? hpMode,
        BootstrapTrigger trigger,
        IChangeContext context,
        CancellationToken ct) =>
        CharacterBootstrapApplier.ApplyCreationBootstrapAsync(
            _bootstrap, character, activeSystem, explicitMaxHp, explicitCurrentHp, trigger, context, hpMode, ct);

    /// <summary>
    /// Surfaces half-wired characters (declared class with no matching pools, stale proficiency, unresolved feats, ...)
    /// at the moment they are written, when the caller can still fix them in the same commit.
    /// </summary>
    internal static async Task RecordWiringFindingsAsync(
        IChangeContext context,
        CharacterWiringAuditor? auditor,
        Character character,
        string system,
        CampaignConfig? config)
    {
        if (auditor is null)
        {
            return;
        }

        var ctx = (ChangeContext)context;
        var findings = await auditor.AuditAsync(ctx.Session, ctx.CampaignName, character, system, config);
        if (findings.Count > 0)
        {
            context.RecordMessage($"[WIRING] {CharacterWiringAuditor.Format(character, findings)}");
        }
    }

    internal static void RecordBootstrapReport(IChangeContext context, BootstrapReport report)
    {
        var ctx = (ChangeContext)context;
        foreach (var message in report.Messages)
        {
            context.RecordMessage(message);
        }

        foreach (var hint in report.LlmHints)
        {
            context.RecordMessage($"[BOOTSTRAP HINT] {hint}");
        }
    }
}

public class LevelUpChangeHandler : IWorldChangeHandler
{
    private readonly CampaignDocumentKeys _keys;
    private readonly CharacterBootstrapOrchestrator _bootstrap;
    private readonly ResourcePoolInitializer _poolInitializer;
    private readonly LevelUpPlanner? _planner;

    public LevelUpChangeHandler(
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap,
        ResourcePoolInitializer poolInitializer,
        LevelUpPlanner? planner = null)
    {
        _planner = planner;
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _poolInitializer = poolInitializer ?? throw new ArgumentNullException(nameof(poolInitializer));
    }

    public bool ShouldHandle(WorldChange change) => change is LevelUpChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var levelUp = (LevelUpChange)change;
        if (string.IsNullOrWhiteSpace(levelUp.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (levelUp.LevelsGained <= 0)
        {
            return ChangeHandlerResult.Failure("levelsGained must be positive.");
        }

        if (!ctx.Characters.TryGetValue(levelUp.CharacterId, out var character))
        {
            character = await ctx.Session.LoadAsync<Character>(levelUp.CharacterId, ct);
            if (character == null)
            {
                return ChangeHandlerResult.Failure($"Character '{levelUp.CharacterId}' not found.");
            }

            ctx.RegisterNewCharacter(character);
        }

        if (!string.IsNullOrEmpty(ctx.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, ctx.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        if (!character.IsPc && !character.IsPartyCompanion)
        {
            return ChangeHandlerResult.Failure(
                $"level_up applies only to player characters (isPc: true) or party companions (isPartyCompanion: true). '{levelUp.CharacterId}' is neither.");
        }

        var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(ctx, _keys, ct);

        // The picks go in before the level's hit points are derived, so a Constitution improvement counts for them.
        var pickMessages = new List<string>();
        var plan = _planner?.Plan(character, activeSystem, levelUp.ClassGained, levelUp.Picks);
        if (levelUp.Picks is { Count: > 0 } picks)
        {
            if (levelUp.LevelsGained != 1)
            {
                return ChangeHandlerResult.Failure("picks apply to one level at a time: set levelsGained to 1.");
            }

            if (plan is null)
            {
                return ChangeHandlerResult.Failure(
                    $"No authored progression for {character.ClassLevel ?? "this character's class"} ({activeSystem}), so picks can't be checked. Use 'choices' to record them.");
            }

            var problems = _planner!.Validate(plan, character, picks);
            if (problems.Count > 0)
            {
                return ChangeHandlerResult.Failure($"level_up picks refused: {string.Join(" ", problems)}");
            }

            pickMessages.AddRange(_planner.Apply(plan, character, picks));
        }
        else if (plan is { Slots.Count: > 0 } && levelUp.LevelsGained == 1 && plan.Slots.Any(s => s.Required))
        {
            ctx.RecordMessage(
                $"Warning: level {plan.CharacterLevel} of {plan.ClassName} asks for {string.Join(", ", plan.Slots.Where(s => s.Required).Select(s => s.Id))}; "
                + "none were given in 'picks', so they aren't applied. Ask the player (lookup kind:'level_up' lists the options) and commit them as picks.");
        }

        var previousMax = character.MaxHp;
        var report = await _bootstrap.ApplyLevelGainAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = activeSystem,
            LevelsGained = levelUp.LevelsGained,
            ClassGained = levelUp.ClassGained,
            HpModeOverride = levelUp.HpMode,
            Trigger = BootstrapTrigger.LevelUp,
            EquipmentAccess = new SessionEquipmentAccess(ctx.Session),
            CampaignName = ctx.CampaignName,
        }, ct);

        CharacterCreateHandler.RecordBootstrapReport(ctx, report);

        var hpStepRan = report.Steps.Any(s => s.StepName.Contains("hit_points", StringComparison.Ordinal));
        if (character.SystemStats?.StatBlockHp is > 0 && !hpStepRan)
        {
            ctx.RecordMessage(
                $"Warning: level_up for '{levelUp.CharacterId}' skipped formula HP gain because systemStats.statBlockHp "
                + $"({character.SystemStats.StatBlockHp}) is set. Remove statBlockHp for leveled PCs, or patch maxHp manually.");
        }

        if (report.Steps.Count == 0)
        {
            ctx.RecordMessage(
                $"Warning: level_up for '{levelUp.CharacterId}' applied no ruleset changes. "
                + "Ensure systemStats has bootstrap fields (5e: hitDie/level/constitution; pf2e: classHpPerLevel/ancestryHp/level) "
                + "and the campaign active ruleset supports level_up.");
        }

        if (levelUp.HealToMatch && character.MaxHp > previousMax)
        {
            character.CurrentHp += character.MaxHp - previousMax;
        }

        var campaignConfig = !string.IsNullOrEmpty(ctx.CampaignName)
            ? await ctx.Session.LoadAsync<CampaignConfig>(_keys.Config(ctx.CampaignName), ct)
            : null;
        _poolInitializer.InitializePools(character, activeSystem, campaignConfig);

        // Don't echo levelUp.Reason back — the caller just supplied that exact text in this same
        // request. The resulting MaxHp is ruleset-formula-derived (hit die rolls, CON mod, etc.), not
        // something the caller could compute itself.
        ctx.RecordMessage(
            $"Level up: {character.Name} gained {levelUp.LevelsGained} level(s). MaxHp {previousMax} → {character.MaxHp}.");

        if (pickMessages.Count > 0)
        {
            ctx.RecordMessage($"{character.Name} chose: {string.Join("; ", pickMessages)}.");
        }

        ApplyLevelUpChoices(character, levelUp, ctx);

        return ChangeHandlerResult.Ok;
    }

    private static void ApplyLevelUpChoices(Character character, LevelUpChange levelUp, IChangeContext context)
    {
        var ctx = (ChangeContext)context;
        if (character.SystemStats == null)
        {
            return;
        }

        if (levelUp.Choices is { Count: > 0 } choices)
        {
            var newLevel = XpThresholdCalculator.GetCurrentLevel(character);
            foreach (var (key, value) in choices)
            {
                character.SystemStats.LevelUpChoices.Add(new LevelUpChoiceRecord
                {
                    Level = newLevel,
                    Key = key,
                    Value = value,
                });
            }
        }

        if (levelUp.AbilityScoreIncreases is { Count: > 0 } increases)
        {
            if (character.SystemStats is Dnd5eExtension dnd5e)
            {
                foreach (var (ability, amount) in increases)
                {
                    ApplyAbilityScoreIncrease(dnd5e, ability, amount);
                }
            }
            else
            {
                context.RecordMessage(
                    "Warning: abilityScoreIncreases on level_up is only applied for D&D 5e characters; ignored for this character's system.");
            }
        }
    }

    private static void ApplyAbilityScoreIncrease(Dnd5eExtension stats, string ability, int amount)
    {
        switch (ability.ToLowerInvariant())
        {
            case "strength": stats.Strength += amount; break;
            case "dexterity": stats.Dexterity += amount; break;
            case "constitution": stats.Constitution += amount; break;
            case "intelligence": stats.Intelligence += amount; break;
            case "wisdom": stats.Wisdom += amount; break;
            case "charisma": stats.Charisma += amount; break;
        }
    }
}

public class ScheduleChangeHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ScheduleChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var sc = (ScheduleChange)change;
        if (!ctx.Characters.TryGetValue(sc.CharacterId, out var c))
        {
            c = await ctx.Session.LoadAsync<Character>(sc.CharacterId, ct);
            if (c == null)
            {
                var hints = await ctx.SuggestCharacterMatchAsync(sc.CharacterId);
                var msg = $"Character {sc.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                return ChangeHandlerResult.Failure(msg);
            }

            ctx.RegisterNewCharacter(c);
        }

        if (!string.IsNullOrEmpty(ctx.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(c, ctx.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        c.Schedule = sc.Schedule;

        return ChangeHandlerResult.Ok;
    }
}

public class CharacterUpdateHandler : IWorldChangeHandler
{
    private readonly CampaignDocumentKeys _keys;
    private readonly CharacterBootstrapOrchestrator _bootstrap;
    private readonly ResourcePoolInitializer? _poolInitializer;
    private readonly CharacterWiringAuditor? _auditor;

    public CharacterUpdateHandler(
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap,
        ResourcePoolInitializer? poolInitializer = null,
        CharacterWiringAuditor? auditor = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _poolInitializer = poolInitializer;
        _auditor = auditor;
    }

    public bool ShouldHandle(WorldChange change) => change is CharacterUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var cu = (CharacterUpdate)change;
        if (string.IsNullOrWhiteSpace(cu.CharacterId)) return ChangeHandlerResult.Failure("characterId is required.");

        var character = await ctx.Session.LoadAsync<Character>(cu.CharacterId, ct);
        if (character == null)
            return ChangeHandlerResult.Failure($"Character '{cu.CharacterId}' not found. Cannot update.");

        if (!string.IsNullOrEmpty(ctx.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, ctx.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        if (cu.LifeStage is { } requestedStage)
        {
            if (!LifeStageRules.TryChange(character.LifeStage, requestedStage, out var stage, out var stageError))
            {
                return ChangeHandlerResult.Failure(stageError!);
            }

            character.LifeStage = stage;
        }

        var appearanceBefore = character.CurrentAppearance;
        var tagsBefore = new HashSet<string>(character.VisualTags);
        var featuresBefore = new HashSet<string>(character.DistinctiveFeatures);
        var keepAliveBefore = character.KeepAlive;

        if (cu.AppearanceOverride != null) character.CurrentAppearance = cu.AppearanceOverride;

        if (cu.TagsToAdd != null)
        {
            character.VisualTags = [.. character.VisualTags.Union(cu.TagsToAdd).Distinct()];
        }

        if (cu.TagsToRemove != null)
        {
            character.VisualTags.RemoveAll(t => cu.TagsToRemove.Contains(t));
            foreach (var removed in cu.TagsToRemove) character.TagProvenance.Remove(removed);
        }

        if (cu.FeaturesToAdd != null)
        {
            character.DistinctiveFeatures = [.. character.DistinctiveFeatures.Union(cu.FeaturesToAdd).Distinct()];
        }

        if (cu.FeaturesToRemove != null)
        {
            character.DistinctiveFeatures.RemoveAll(f => cu.FeaturesToRemove.Contains(f));
            foreach (var removed in cu.FeaturesToRemove) character.TagProvenance.Remove(removed);
        }

        // Appearance/features are otherwise only recoverable from conversation memory, which is lossy
        // across ctx compaction. Auto-log a low-weight history entry so recall_history/NpcRecentEvents
        // can surface *when* this changed, without requiring the caller to issue a second `event` commit.
        var appearanceChanged = character.CurrentAppearance != appearanceBefore
            || !tagsBefore.SetEquals(character.VisualTags)
            || !featuresBefore.SetEquals(character.DistinctiveFeatures);

        if (appearanceChanged)
        {
            // Echo the CURRENT merged appearance/tags (not just this change's diff) — AppearanceOverride
            // is overwrite semantics, so if this update replaced CurrentAppearance without restating an
            // earlier detail (e.g. a wound, restraint, combat residue), this is the model's one chance to
            // notice the drop before it narrates from a now-stale mental picture.
            var tagsText = character.VisualTags.Count > 0 ? $" Tags: [{string.Join(", ", character.VisualTags)}]." : string.Empty;
            ctx.RecordPhysicalStateNudge(
                $"{character.Name}'s current appearance: {character.CurrentAppearance ?? "(no override set)"}.{tagsText}");

            var eventId = "events/" + Guid.NewGuid();
            await ctx.LogEventAsync(new Event
            {
                Id = eventId,
                Summary = $"{character.Name}'s appearance changed: {character.CurrentAppearance ?? "(no override)"}; tags: [{string.Join(", ", character.VisualTags)}]",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                Involved = [cu.CharacterId],
                LocationId = character.CurrentLocationId,
                DayLogged = (await ctx.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = ctx.CampaignName,
            });

            // Ground-truth provenance: which event established this specific fact. Kept separate from
            // this character's own subjective PsychologyProfile.Memories (which may misremember it).
            if (character.CurrentAppearance != appearanceBefore)
            {
                if (appearanceBefore != null) character.TagProvenance.Remove(appearanceBefore);
                if (character.CurrentAppearance != null) character.TagProvenance[character.CurrentAppearance] = [eventId];
            }
            foreach (var addedTag in character.VisualTags.Except(tagsBefore))
            {
                character.TagProvenance[addedTag] = [eventId];
            }
            foreach (var addedFeature in character.DistinctiveFeatures.Except(featuresBefore))
            {
                character.TagProvenance[addedFeature] = [eventId];
            }
        }

        if (cu.KeepAlive.HasValue)
        {
            character.KeepAlive = cu.KeepAlive.Value;

            // Nudge: NPC promoted from transient to permanent — suggest creating a plot thread
            if (!keepAliveBefore && cu.KeepAlive.Value)
            {
                ctx.RecordMessage(
                    $"NARRATIVE PROMPT: '{character.Name}' promoted from transient to permanent NPC. Consider creating a plot thread " +
                    $"(\"little story\") for them with clues, foreshadowing, and resolution conditions. " +
                    $"Use world_build with plotThreads[] to seed it, or get_entity('plot-threads') to list existing threads.");
            }
        }

        if (cu.IsPc.HasValue || cu.IsPartyCompanion.HasValue)
        {
            var newIsPc = cu.IsPc ?? character.IsPc;
            var newIsCompanion = cu.IsPartyCompanion ?? character.IsPartyCompanion;
            if (cu.IsPc == true)
            {
                newIsCompanion = false;
            }
            else if (cu.IsPartyCompanion == true)
            {
                newIsPc = false;
            }

            if (!CharacterPartyRules.TryValidate(newIsPc, newIsCompanion, character.CampaignName, out var partyError))
            {
                return ChangeHandlerResult.Failure(partyError!);
            }

            character.IsPc = newIsPc;
            character.IsPartyCompanion = newIsCompanion;

            // Force KeepAlive = true if flipping IsPc or IsPartyCompanion to true
            if (newIsPc || newIsCompanion)
            {
                character.KeepAlive = true;
            }
        }

        if (cu.SystemStats != null)
        {
            var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(ctx, _keys, ct);
            if (!SystemStatsMerger.TryValidateRuleset(cu.SystemStats, activeSystem, out var validationError))
            {
                return ChangeHandlerResult.Failure(validationError!);
            }

            character.SystemStats = SystemStatsMerger.Merge(
                character.SystemStats ?? SystemStatsMerger.CreateDefault(activeSystem),
                SystemStatsMerger.CoerceToRuleset(cu.SystemStats, activeSystem),
                activeSystem);

            await CharacterBootstrapApplier.ApplyCreationBootstrapAsync(
                _bootstrap, character, activeSystem, null, null, BootstrapTrigger.SystemStatsPatch, ctx, ct: ct);

            // A patch can add classes/levels/feats; without this the pools those imply (action_surge, spell
            // slots, ...) were only ever created by character_create and level_up, so patched-in classes stayed pool-less.
            var patchConfig = !string.IsNullOrEmpty(ctx.CampaignName)
                ? await ctx.Session.LoadAsync<CampaignConfig>(_keys.Config(ctx.CampaignName), ct)
                : null;
            _poolInitializer?.InitializePools(character, activeSystem, patchConfig);
            await CharacterCreateHandler.RecordWiringFindingsAsync(ctx, _auditor, character, activeSystem, patchConfig);
        }

        if (cu.DepartedAtDay.HasValue)
        {
            character.DepartedAtDay = cu.DepartedAtDay;
        }

        if (cu.DepartedFromLocationId != null)
        {
            character.DepartedFromLocationId = string.IsNullOrWhiteSpace(cu.DepartedFromLocationId)
                ? null
                : cu.DepartedFromLocationId;
        }

        if (cu.ClearDeparture == true)
        {
            character.DepartedAtDay = null;
            character.DepartedFromLocationId = null;
        }

        if (cu.ControlledById != null || cu.MinionBinding != null)
        {
            var (updateLinkError, updateController) = await MinionLinkApplier.ResolveControllerAsync(
                ctx, character.Id, cu.ControlledById, cu.MinionBinding, ct);
            if (updateLinkError != null)
            {
                return ChangeHandlerResult.Failure(updateLinkError);
            }

            if (updateController != null)
            {
                MinionLinkApplier.ApplyLink(character, updateController, cu.MinionBinding);
            }
        }

        if (cu.ClearMinionLink == true)
        {
            MinionLinkApplier.ClearLink(character);
        }

        MinionLinkApplier.ApplyListDelta(character, cu.ControlsMinionIdsAdd, cu.ControlsMinionIdsRemove);

        return ChangeHandlerResult.Ok;
    }
}

/// <summary>
/// Shared minion-link application for character_create / character_update (and the
/// engine-emitted summon, dismiss, cap-release, and lapse mutations, which flow
/// through the same handlers). All operations are idempotent no-ops when the link
/// is already in the requested state, so retried or half-paired batches converge
/// instead of failing.
/// </summary>
internal static class MinionLinkApplier
{
    /// <summary>
    /// Resolves the effective controller from an explicit id and/or a binding,
    /// failing when they disagree, when the controller is the character itself,
    /// or when the controller does not exist. Null controller means "no link".
    /// </summary>
    public static async Task<(string? Error, string? Controller)> ResolveControllerAsync(
        ChangeContext ctx, string characterId, string? controlledById, MinionBinding? binding,
        CancellationToken ct)
    {
        var controller = controlledById ?? binding?.ControllerId;
        if (string.IsNullOrWhiteSpace(controller))
        {
            return (null, null);
        }

        if (controlledById != null && binding?.ControllerId != null
            && !string.Equals(controlledById, binding.ControllerId, StringComparison.Ordinal))
        {
            return ($"controlledById '{controlledById}' disagrees with minionBinding.controllerId '{binding.ControllerId}'.", null);
        }

        if (string.Equals(controller, characterId, StringComparison.Ordinal))
        {
            return ($"Character '{characterId}' cannot control itself as a minion.", null);
        }

        if (!ctx.Characters.ContainsKey(controller)
            && await ctx.Session.LoadAsync<Character>(controller, ct) == null)
        {
            return ($"Minion controller '{controller}' does not exist.", null);
        }

        return (null, controller);
    }

    public static void ApplyLink(Character character, string controller, MinionBinding? binding)
    {
        character.ControlledById = controller;
        if (binding != null)
        {
            var retargeted = binding.ControllerId == controller
                ? binding
                : binding with { ControllerId = controller };
            character.MinionBinding = retargeted.ControlLapsed
                ? retargeted with { ControlLapsed = false }
                : retargeted;
        }
        else if (character.MinionBinding?.ControllerId != controller)
        {
            character.MinionBinding = character.MinionBinding == null
                ? new MinionBinding { ControllerId = controller }
                : character.MinionBinding with { ControllerId = controller, ControlLapsed = false };
        }
        else if (character.MinionBinding is { ControlLapsed: true } lapsed)
        {
            character.MinionBinding = lapsed with { ControlLapsed = false };
        }
    }

    /// <summary>
    /// Ends live control. The binding is kept as a lapsed record (disposition
    /// preserved) so the GM can see what the released minion is and how it now
    /// regards its former controller; the body stays as an ordinary NPC.
    /// </summary>
    public static void ClearLink(Character character)
    {
        character.ControlledById = null;
        if (character.MinionBinding != null)
        {
            character.MinionBinding = character.MinionBinding with { ControlLapsed = true };
        }
    }

    public static void ApplyListDelta(Character character, List<string>? add, List<string>? remove)
    {
        if (remove != null && remove.Count > 0)
        {
            var doomed = new HashSet<string>(remove, StringComparer.Ordinal);
            character.ControlsMinionIds.RemoveAll(id => doomed.Contains(id));
        }

        if (add != null && add.Count > 0)
        {
            var known = new HashSet<string>(character.ControlsMinionIds, StringComparer.Ordinal);
            foreach (var id in add.Where(id => !string.IsNullOrWhiteSpace(id) && known.Add(id)))
            {
                character.ControlsMinionIds.Add(id);
            }
        }
    }
}

internal static class CharacterHandlerHelpers
{
    public static async Task<string> ResolveActiveSystemAsync(IChangeContext context, CampaignDocumentKeys keys,
        CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        if (string.IsNullOrEmpty(context.CampaignName))
        {
            throw new InvalidOperationException("Cannot resolve the active ruleset system without a campaign name.");
        }

        var configId = keys.Config(context.CampaignName);
        var config = await ctx.Session.LoadAsync<CampaignConfig>(configId, ct);
        return config?.ActiveSystem
            ?? throw new InvalidOperationException(
                $"No campaign config found for '{context.CampaignName}'; cannot determine its ruleset system.");
    }
}

public class KnowledgeUpdateHandler(ILocalEmbeddingService embeddingService) : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is KnowledgeUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context,
        CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var ku = (KnowledgeUpdate)change;
        if (string.IsNullOrWhiteSpace(ku.CharacterId)) return ChangeHandlerResult.Failure("characterId is required.");
        if (string.IsNullOrWhiteSpace(ku.Topic)) return ChangeHandlerResult.Failure("topic is required.");
        if (ku.CreateMemory && string.IsNullOrWhiteSpace(ku.Details))
            return ChangeHandlerResult.Failure("details is required when createMemory is true.");

        if (!ku.CreateMemory)
        {
            ctx.RecordMessage(
                $"Skipped memory update for '{ku.CharacterId}' topic '{ku.Topic}' (createMemory=false).");
            return ChangeHandlerResult.Ok;
        }

        var character = await ctx.Session.LoadAsync<Character>(ku.CharacterId, ct);
        if (character == null)
            return ChangeHandlerResult.Failure($"Character '{ku.CharacterId}' not found. Cannot update knowledge.");

        if (!string.IsNullOrEmpty(ctx.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, ctx.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        var isNew = !character.Psychology.Memories.TryGetValue(ku.Topic, out var memory);
        if (isNew)
        {
            memory = new MemoryNode { Topic = ku.Topic };
            character.Psychology.Memories[ku.Topic] = memory;
        }
        else
        {
            memory!.ApplyMigrationDefaultsIfNeeded();

            // Self-heal a legacy node whose Topic was lost: the caller addressed it by this exact
            // topic, so it's the caller's identity, not an engine invention. This is the repair
            // EntityIntegrityPressureContributor's null-Topic warning suggests.
            if (string.IsNullOrWhiteSpace(memory.Topic))
            {
                memory.Topic = ku.Topic;
            }
        }

        memory.Details = ku.Details;
        character.LastUpdated = DateTime.UtcNow;
        var time = await ctx.GetCurrentTimeAsync();

        if (isNew)
        {
            // New memory: set DayAcquired to now
            memory.DayAcquired = (int)time.TotalDaysElapsed;
        }
        else
        {
            // Existing memory: nudge salience up instead of resetting DayAcquired (so decay tracking stays honest)
            // UNLESS this is a Deliberate re-recording, which reasserts full salience
            if (ku.RecordingMode != RecordingMode.Deliberate)
            {
                memory.Salience = Math.Clamp(memory.Salience + 0.1, 0.0, 1.0);
            }
        }

        // Handle Importance: explicit value takes precedence, then Deliberate floor, then defaults
        if (ku.Importance.HasValue)
        {
            memory.Importance = ku.Importance.Value;
        }
        else if (ku.RecordingMode == RecordingMode.Deliberate && memory.Importance == MemoryImportance.Trivial)
        {
            // Deliberate recording floors at Important unless explicitly set lower
            memory.Importance = MemoryImportance.Important;
        }

        ApplyEnrichment(memory, ku, isNew);

        if ((ku.Source is MemorySource.Witnessed or MemorySource.Experienced)
            && (ku.SourceEventIds == null || ku.SourceEventIds.Count == 0))
        {
            return ChangeHandlerResult.Failure(
                $"knowledge_update for '{ku.CharacterId}' topic '{ku.Topic}' has source={memory.Source} (directly event-sourced) "
                + "but no sourceEventIds. Pass a client-chosen eventId on the paired event change in this same batch and reference it here.");
        }

        await SemanticEnrichmentHelper.EnrichAsync(memory, embeddingService, ctx.Logger, ct);

        return ChangeHandlerResult.Ok;
    }

    private static void ApplyEnrichment(MemoryNode memory, KnowledgeUpdate ku, bool isNew)
    {
        var isDeliberate = ku.RecordingMode == RecordingMode.Deliberate;

        if (isNew && !isDeliberate)
        {
            // Only infer defaults from text for Passive mode (Deliberate act is the strong signal)
            InferDefaultsFromDetails(memory, ku.Details);
        }

        if (ku.Source.HasValue)
        {
            memory.Source = ku.Source.Value;
        }

        if (ku.Valence.HasValue)
        {
            memory.Valence = ku.Valence.Value;
        }

        if (ku.Salience.HasValue)
        {
            memory.Salience = Math.Clamp(ku.Salience.Value, 0.0, 1.0);
        }
        else if (isDeliberate)
        {
            // Deliberate recording locks in maximum salience
            memory.Salience = 1.0;
        }

        if (ku.Urgency.HasValue)
        {
            memory.Urgency = ku.Urgency.Value;
        }

        if (ku.RelatedEntityIds != null)
        {
            memory.RelatedEntityIds = ku.RelatedEntityIds;
        }

        if (ku.SourceEventIds != null)
        {
            memory.SourceEventIds = ku.SourceEventIds;
        }
    }

    private static void InferDefaultsFromDetails(MemoryNode memory, string details)
    {
        var text = details.AsSpan();
        if (ContainsAny(text, "trauma", "traumatic", "nightmare", "ptsd"))
        {
            memory.Valence = EmotionalValence.Traumatic;
            memory.Source = MemorySource.Trauma;
            memory.Urgency = MemoryUrgency.High;
            memory.Salience = 0.85;
            return;
        }

        if (ContainsAny(text, "heard", "overheard", "rumor", "rumour"))
        {
            memory.Source = MemorySource.Heard;
        }

        if (ContainsAny(text, "love", "grateful", "kindness", "gift", "friend", "trust"))
        {
            memory.Valence = EmotionalValence.Positive;
            memory.Salience = Math.Max(memory.Salience, 0.65);
        }
        else if (ContainsAny(text, "hate", "betray", "fear", "danger", "violence", "death", "murder"))
        {
            memory.Valence = EmotionalValence.Negative;
            memory.Salience = Math.Max(memory.Salience, 0.7);
            memory.Urgency = MemoryUrgency.High;
        }
    }

    private static bool ContainsAny(ReadOnlySpan<char> text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}