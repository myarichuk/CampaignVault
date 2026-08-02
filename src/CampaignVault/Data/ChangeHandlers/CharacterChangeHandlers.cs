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

    public CharacterCreateHandler(
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap,
        ResourcePoolInitializer poolInitializer,
        ClassDefinitionProvider classProvider)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _poolInitializer = poolInitializer ?? throw new ArgumentNullException(nameof(poolInitializer));
        _classProvider = classProvider ?? throw new ArgumentNullException(nameof(classProvider));
    }

    public bool ShouldHandle(WorldChange change) => change is CharacterCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var cc = (CharacterCreate)change;
        if (string.IsNullOrWhiteSpace(cc.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        var existing = context.Session != null ? await context.Session.LoadAsync<Character>(cc.CharacterId, ct) : null;
        if (existing != null)
        {
            if (!string.IsNullOrEmpty(context.CampaignName)
                && CampaignEntityVisibility.TryGetInvisibilityReason(existing, context.CampaignName, out var hidden))
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

            if (cc.IsPc || cc.IsPartyCompanion || existing.IsPc || existing.IsPartyCompanion)
            {
                var mergedIsPc = cc.IsPc || existing.IsPc;
                var mergedCompanion = cc.IsPartyCompanion || existing.IsPartyCompanion;
                if (!CharacterPartyRules.TryValidate(mergedIsPc, mergedCompanion, existing.CampaignName ?? context.CampaignName,
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

            if (cc.SystemStats != null)
            {
                var existingSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);
                if (!SystemStatsMerger.TryValidateRuleset(cc.SystemStats, existingSystem,
                        out var existingValidationError))
                {
                    return ChangeHandlerResult.Failure(existingValidationError!);
                }

                existing.SystemStats = SystemStatsMerger.Merge(
                    existing.SystemStats ?? SystemStatsMerger.CreateDefault(existingSystem),
                    SystemStatsMerger.CoerceToRuleset(cc.SystemStats, existingSystem));
            }

            var activeSystemForExisting =
                await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);
            await ApplyBootstrapAsync(existing, activeSystemForExisting, cc.MaxHp, cc.CurrentHp, null,
                BootstrapTrigger.Create, context, ct);

            // Reinitialize resource pools if needed (in case level/class changed)
            var campaignConfigExisting = context.Session != null && !string.IsNullOrEmpty(context.CampaignName)
                ? await context.Session.LoadAsync<CampaignConfig>(_keys.Config(context.CampaignName), ct)
                : null;
            _poolInitializer.InitializePools(existing, activeSystemForExisting, campaignConfigExisting);

            var hint = existing.KeepAlive
                ? " For existing PCs, prefer commit with activity/character_update instead of character_create. Call get_party to confirm PCs already exist."
                : string.Empty;
            context.RecordEntityCollision(cc.CharacterId,
                $"Warning: Character {cc.CharacterId} already exists. Updated existing character fields.{hint}");
            return ChangeHandlerResult.Ok;
        }

        var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);

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
                SystemStatsMerger.CoerceToRuleset(cc.SystemStats, activeSystem));
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
            Schedule = cc.Schedule,
            Psychology = cc.Psychology ?? new PsychologyProfile(),
            ClassLevel = cc.ClassLevel,
            MaxHp = cc.MaxHp ?? 0,
            CurrentHp = cc.CurrentHp ?? cc.MaxHp ?? 0,
            SystemStats = systemStats
        };

        if (string.IsNullOrEmpty(newChar.CampaignName))
        {
            newChar.CampaignName = context.CampaignName;
        }

        if (!CharacterPartyRules.TryValidate(newChar.IsPc, newChar.IsPartyCompanion, newChar.CampaignName,
                out var createPartyError))
        {
            return ChangeHandlerResult.Failure(createPartyError!);
        }

        await ApplyBootstrapAsync(newChar, activeSystem, cc.MaxHp, cc.CurrentHp, null, BootstrapTrigger.Create, context, ct);

        // Initialize resource pools (spell slots, focus points, action points, etc.)
        var campaignConfig = context.Session != null && !string.IsNullOrEmpty(context.CampaignName)
            ? await context.Session.LoadAsync<CampaignConfig>(_keys.Config(context.CampaignName), ct)
            : null;
        _poolInitializer.InitializePools(newChar, activeSystem, campaignConfig);

        RecordClassResolutionEcho(context, newChar, activeSystem, cc.ClassLevel);

        await context.Session!.StoreAsync(newChar, ct);
        context.RegisterNewCharacter(newChar);

        return ChangeHandlerResult.Ok;
    }

    private void RecordClassResolutionEcho(
        ChangeContext context,
        Character character,
        string system,
        string? classLevelInput)
    {
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
        ChangeContext context,
        CancellationToken ct) =>
        CharacterBootstrapApplier.ApplyCreationBootstrapAsync(
            _bootstrap, character, activeSystem, explicitMaxHp, explicitCurrentHp, trigger, context, hpMode, ct);

    internal static void RecordBootstrapReport(ChangeContext context, BootstrapReport report)
    {
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

    public LevelUpChangeHandler(
        CampaignDocumentKeys keys,
        CharacterBootstrapOrchestrator bootstrap,
        ResourcePoolInitializer poolInitializer)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _poolInitializer = poolInitializer ?? throw new ArgumentNullException(nameof(poolInitializer));
    }

    public bool ShouldHandle(WorldChange change) => change is LevelUpChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var levelUp = (LevelUpChange)change;
        if (string.IsNullOrWhiteSpace(levelUp.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (levelUp.LevelsGained <= 0)
        {
            return ChangeHandlerResult.Failure("levelsGained must be positive.");
        }

        if (!context.Characters.TryGetValue(levelUp.CharacterId, out var character))
        {
            character = context.Session != null
                ? await context.Session.LoadAsync<Character>(levelUp.CharacterId, ct)
                : null;
            if (character == null)
            {
                return ChangeHandlerResult.Failure($"Character '{levelUp.CharacterId}' not found.");
            }

            context.RegisterNewCharacter(character);
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, context.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        if (!character.IsPc && !character.IsPartyCompanion)
        {
            return ChangeHandlerResult.Failure(
                $"level_up applies only to player characters (isPc: true) or party companions (isPartyCompanion: true). '{levelUp.CharacterId}' is neither.");
        }

        var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);
        var previousMax = character.MaxHp;
        var report = await _bootstrap.ApplyLevelGainAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = activeSystem,
            LevelsGained = levelUp.LevelsGained,
            ClassGained = levelUp.ClassGained,
            HpModeOverride = levelUp.HpMode,
            Trigger = BootstrapTrigger.LevelUp,
            Session = context.Session,
            CampaignName = context.CampaignName,
        }, ct);

        CharacterCreateHandler.RecordBootstrapReport(context, report);

        var hpStepRan = report.Steps.Any(s => s.StepName.Contains("hit_points", StringComparison.Ordinal));
        if (character.SystemStats?.StatBlockHp is > 0 && !hpStepRan)
        {
            context.RecordMessage(
                $"Warning: level_up for '{levelUp.CharacterId}' skipped formula HP gain because systemStats.statBlockHp "
                + $"({character.SystemStats.StatBlockHp}) is set. Remove statBlockHp for leveled PCs, or patch maxHp manually.");
        }

        if (report.Steps.Count == 0)
        {
            context.RecordMessage(
                $"Warning: level_up for '{levelUp.CharacterId}' applied no ruleset changes. "
                + "Ensure systemStats has bootstrap fields (5e: hitDie/level/constitution; pf2e: classHpPerLevel/ancestryHp/level) "
                + "and the campaign active ruleset supports level_up.");
        }

        if (levelUp.HealToMatch && character.MaxHp > previousMax)
        {
            character.CurrentHp += character.MaxHp - previousMax;
        }

        var campaignConfig = context.Session != null && !string.IsNullOrEmpty(context.CampaignName)
            ? await context.Session.LoadAsync<CampaignConfig>(_keys.Config(context.CampaignName), ct)
            : null;
        _poolInitializer.InitializePools(character, activeSystem, campaignConfig);

        var reasonSuffix = string.IsNullOrWhiteSpace(levelUp.Reason) ? "" : $" ({levelUp.Reason})";
        context.RecordMessage(
            $"Level up: {character.Name} gained {levelUp.LevelsGained} level(s){reasonSuffix}. MaxHp {previousMax} → {character.MaxHp}.");

        ApplyLevelUpChoices(character, levelUp, context);

        return ChangeHandlerResult.Ok;
    }

    private static void ApplyLevelUpChoices(Character character, LevelUpChange levelUp, ChangeContext context)
    {
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

            context.RecordMessage(
                $"Level-up choices recorded for {character.Name}: {string.Join(", ", choices.Select(kv => $"{kv.Key}={kv.Value}"))}.");
        }

        if (levelUp.AbilityScoreIncreases is { Count: > 0 } increases)
        {
            if (character.SystemStats is Dnd5eExtension dnd5e)
            {
                foreach (var (ability, amount) in increases)
                {
                    ApplyAbilityScoreIncrease(dnd5e, ability, amount);
                }

                context.RecordMessage(
                    $"Ability score increase for {character.Name}: {string.Join(", ", increases.Select(kv => $"{kv.Key} +{kv.Value}"))}.");
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

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var sc = (ScheduleChange)change;
        if (!context.Characters.TryGetValue(sc.CharacterId, out var c))
        {
            c = context.Session != null ? await context.Session.LoadAsync<Character>(sc.CharacterId, ct) : null;
            if (c == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(sc.CharacterId);
                var msg = $"Character {sc.CharacterId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                return ChangeHandlerResult.Failure(msg);
            }

            context.RegisterNewCharacter(c);
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(c, context.CampaignName, out var hidden))
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

    public CharacterUpdateHandler(CampaignDocumentKeys keys, CharacterBootstrapOrchestrator bootstrap)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
    }

    public bool ShouldHandle(WorldChange change) => change is CharacterUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var cu = (CharacterUpdate)change;
        if (string.IsNullOrWhiteSpace(cu.CharacterId)) return ChangeHandlerResult.Failure("characterId is required.");

        var character = context.Session != null ? await context.Session.LoadAsync<Character>(cu.CharacterId, ct) : null;
        if (character == null)
            return ChangeHandlerResult.Failure($"Character '{cu.CharacterId}' not found. Cannot update.");

        if (!string.IsNullOrEmpty(context.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, context.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        var appearanceBefore = character.CurrentAppearance;
        var tagsBefore = new HashSet<string>(character.VisualTags);
        var featuresBefore = new HashSet<string>(character.DistinctiveFeatures);
        var keepAliveBefore = character.KeepAlive;

        if (cu.AppearanceOverride != null) character.CurrentAppearance = cu.AppearanceOverride;

        if (cu.TagsToAdd != null)
        {
            character.VisualTags = character.VisualTags.Union(cu.TagsToAdd).Distinct().ToList();
        }

        if (cu.TagsToRemove != null)
        {
            character.VisualTags.RemoveAll(t => cu.TagsToRemove.Contains(t));
            foreach (var removed in cu.TagsToRemove) character.TagProvenance.Remove(removed);
        }

        if (cu.FeaturesToAdd != null)
        {
            character.DistinctiveFeatures = character.DistinctiveFeatures.Union(cu.FeaturesToAdd).Distinct().ToList();
        }

        if (cu.FeaturesToRemove != null)
        {
            character.DistinctiveFeatures.RemoveAll(f => cu.FeaturesToRemove.Contains(f));
            foreach (var removed in cu.FeaturesToRemove) character.TagProvenance.Remove(removed);
        }

        // Appearance/features are otherwise only recoverable from conversation memory, which is lossy
        // across context compaction. Auto-log a low-weight history entry so recall_history/NpcRecentEvents
        // can surface *when* this changed, without requiring the caller to issue a second `event` commit.
        var appearanceChanged = character.CurrentAppearance != appearanceBefore
            || !tagsBefore.SetEquals(character.VisualTags)
            || !featuresBefore.SetEquals(character.DistinctiveFeatures);

        if (appearanceChanged)
        {
            var eventId = "events/" + Guid.NewGuid();
            await context.LogEventAsync(new Event
            {
                Id = eventId,
                Summary = $"{character.Name}'s appearance changed: {character.CurrentAppearance ?? "(no override)"}; tags: [{string.Join(", ", character.VisualTags)}]",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                Involved = [cu.CharacterId],
                LocationId = character.CurrentLocationId,
                DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = context.CampaignName,
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
                context.RecordMessage(
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
            var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);
            if (!SystemStatsMerger.TryValidateRuleset(cu.SystemStats, activeSystem, out var validationError))
            {
                return ChangeHandlerResult.Failure(validationError!);
            }

            character.SystemStats = SystemStatsMerger.Merge(
                character.SystemStats ?? SystemStatsMerger.CreateDefault(activeSystem),
                SystemStatsMerger.CoerceToRuleset(cu.SystemStats, activeSystem));

            await CharacterBootstrapApplier.ApplyCreationBootstrapAsync(
                _bootstrap, character, activeSystem, null, null, BootstrapTrigger.SystemStatsPatch, context, ct: ct);
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

        context.RecordMessage($"Updated character '{cu.CharacterId}'.");
        return ChangeHandlerResult.Ok;
    }
}

internal static class CharacterHandlerHelpers
{
    public static async Task<string> ResolveActiveSystemAsync(ChangeContext context, CampaignDocumentKeys keys,
        CancellationToken ct)
    {
        if (context.Session == null || string.IsNullOrEmpty(context.CampaignName))
        {
            return RulesetSystem.Dnd5e;
        }

        var configId = keys.Config(context.CampaignName);
        var config = await context.Session.LoadAsync<CampaignConfig>(configId, ct);
        return config?.ActiveSystem ?? RulesetSystem.Dnd5e;
    }
}

public class KnowledgeUpdateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is KnowledgeUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var ku = (KnowledgeUpdate)change;
        if (string.IsNullOrWhiteSpace(ku.CharacterId)) return ChangeHandlerResult.Failure("characterId is required.");
        if (string.IsNullOrWhiteSpace(ku.Topic)) return ChangeHandlerResult.Failure("topic is required.");

        if (!ku.CreateMemory)
        {
            context.RecordMessage(
                $"Skipped memory update for '{ku.CharacterId}' topic '{ku.Topic}' (createMemory=false).");
            return ChangeHandlerResult.Ok;
        }

        var character = context.Session != null ? await context.Session.LoadAsync<Character>(ku.CharacterId, ct) : null;
        if (character == null)
            return ChangeHandlerResult.Failure($"Character '{ku.CharacterId}' not found. Cannot update knowledge.");

        if (!string.IsNullOrEmpty(context.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, context.CampaignName, out var hidden))
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
        }

        memory.Details = ku.Details;
        character.LastUpdated = DateTime.UtcNow;
        var time = await context.GetCurrentTimeAsync();

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

        if ((memory.Source is MemorySource.Witnessed or MemorySource.Experienced)
            && (memory.SourceEventIds == null || memory.SourceEventIds.Count == 0))
        {
            return ChangeHandlerResult.Failure(
                $"knowledge_update for '{ku.CharacterId}' topic '{ku.Topic}' has source={memory.Source} (directly event-sourced) "
                + "but no sourceEventIds. Pass a client-chosen eventId on the paired event change in this same batch and reference it here.");
        }

        context.RecordMessage($"Updated memory for character '{ku.CharacterId}' regarding '{ku.Topic}'.");
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
        else if (isDeliberate && !ku.Source.HasValue)
        {
            // Deliberate recording defaults to first-person experience when no Source is explicit
            memory.Source = MemorySource.Experienced;
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

        if (ContainsAny(text, "saw", "witnessed", "watched"))
        {
            memory.Source = MemorySource.Witnessed;
        }
        else if (ContainsAny(text, "heard", "overheard", "rumor", "rumour"))
        {
            memory.Source = MemorySource.Heard;
        }
        else if (ContainsAny(text, "lived through", "survived", "experienced"))
        {
            memory.Source = MemorySource.Experienced;
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