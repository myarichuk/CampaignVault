using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Bootstrap;

namespace CampaignVault.Data.ChangeHandlers;

public class CharacterCreateHandler : IWorldChangeHandler
{
    private readonly CampaignDocumentKeys _keys;
    private readonly CharacterBootstrapOrchestrator _bootstrap;

    public CharacterCreateHandler(CampaignDocumentKeys keys, CharacterBootstrapOrchestrator bootstrap)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
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
                existing.CurrentHp = cc.CurrentHp.Value;
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

            var hint = existing.KeepAlive
                ? " For existing PCs, prefer commit with activity/character_update instead of character_create. Call get_party to confirm PCs already exist."
                : string.Empty;
            context.RecordMessage(
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
            KeepAlive = cc.KeepAlive,
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

        await context.Session!.StoreAsync(newChar, ct);
        context.RegisterNewCharacter(newChar);

        return ChangeHandlerResult.Ok;
    }

    private Task ApplyBootstrapAsync(
        Character character,
        RulesetSystem activeSystem,
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

    public LevelUpChangeHandler(CampaignDocumentKeys keys, CharacterBootstrapOrchestrator bootstrap)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
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

        if (!character.IsPc)
        {
            return ChangeHandlerResult.Failure(
                $"level_up applies only to player characters (isPc: true). '{levelUp.CharacterId}' is not a PC.");
        }

        var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);
        var previousMax = character.MaxHp;
        var report = await _bootstrap.ApplyLevelGainAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = activeSystem,
            LevelsGained = levelUp.LevelsGained,
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
                + "Ensure systemStats has bootstrap fields (5e: hitDie/level/constitution; pf2e: classHpPerLevel/ancestryHp/level; "
                + "fallout2d20: endurance/luck/level) and the campaign active ruleset supports level_up.");
        }

        if (levelUp.HealToMatch && character.MaxHp > previousMax)
        {
            character.CurrentHp += character.MaxHp - previousMax;
        }

        return ChangeHandlerResult.Ok;
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

        if (cu.AppearanceOverride != null) character.CurrentAppearance = cu.AppearanceOverride;

        if (cu.TagsToAdd != null)
        {
            character.VisualTags = character.VisualTags.Union(cu.TagsToAdd).Distinct().ToList();
        }

        if (cu.TagsToRemove != null)
        {
            character.VisualTags.RemoveAll(t => cu.TagsToRemove.Contains(t));
        }

        if (cu.FeaturesToAdd != null)
        {
            character.DistinctiveFeatures = character.DistinctiveFeatures.Union(cu.FeaturesToAdd).Distinct().ToList();
        }

        if (cu.FeaturesToRemove != null)
        {
            character.DistinctiveFeatures.RemoveAll(f => cu.FeaturesToRemove.Contains(f));
        }

        if (cu.KeepAlive.HasValue)
        {
            character.KeepAlive = cu.KeepAlive.Value;
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

        context.RecordMessage($"Updated character '{cu.CharacterId}'.");
        return ChangeHandlerResult.Ok;
    }
}

public class SystemStatsChangeHandler : IWorldChangeHandler
{
    private readonly CampaignDocumentKeys _keys;
    private readonly CharacterBootstrapOrchestrator _bootstrap;

    public SystemStatsChangeHandler(CampaignDocumentKeys keys, CharacterBootstrapOrchestrator bootstrap)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
    }

    public bool ShouldHandle(WorldChange change) => change is SystemStatsChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context,
        CancellationToken ct = default)
    {
        var ssc = (SystemStatsChange)change;
        if (string.IsNullOrWhiteSpace(ssc.CharacterId))
        {
            return ChangeHandlerResult.Failure("characterId is required.");
        }

        if (ssc.SystemStats == null)
        {
            return ChangeHandlerResult.Failure("systemStats is required.");
        }

        var character = context.Session != null
            ? await context.Session.LoadAsync<Character>(ssc.CharacterId, ct)
            : null;
        if (character == null)
        {
            return ChangeHandlerResult.Failure($"Character '{ssc.CharacterId}' not found. Cannot update system stats.");
        }

        if (!string.IsNullOrEmpty(context.CampaignName)
            && CampaignEntityVisibility.TryGetInvisibilityReason(character, context.CampaignName, out var hidden))
        {
            return ChangeHandlerResult.Failure(hidden);
        }

        var activeSystem = await CharacterHandlerHelpers.ResolveActiveSystemAsync(context, _keys, ct);
        if (!SystemStatsMerger.TryValidateRuleset(ssc.SystemStats, activeSystem, out var validationError))
        {
            return ChangeHandlerResult.Failure(validationError!);
        }

        character.SystemStats = SystemStatsMerger.Merge(
            character.SystemStats ?? SystemStatsMerger.CreateDefault(activeSystem),
            SystemStatsMerger.CoerceToRuleset(ssc.SystemStats, activeSystem));

        await CharacterBootstrapApplier.ApplyCreationBootstrapAsync(
            _bootstrap, character, activeSystem, null, null, BootstrapTrigger.SystemStatsPatch, context, ct: ct);

        context.RecordMessage($"Updated system stats for '{ssc.CharacterId}'.");
        return ChangeHandlerResult.Ok;
    }
}

internal static class CharacterHandlerHelpers
{
    public static async Task<RulesetSystem> ResolveActiveSystemAsync(ChangeContext context, CampaignDocumentKeys keys,
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
        var time = await context.GetCurrentTimeAsync();
        memory.DayAcquired = (int)time.TotalDaysElapsed;

        if (ku.Importance.HasValue)
        {
            memory.Importance = ku.Importance.Value;
        }

        ApplyEnrichment(memory, ku, isNew);

        context.RecordMessage($"Updated memory for character '{ku.CharacterId}' regarding '{ku.Topic}'.");
        return ChangeHandlerResult.Ok;
    }

    private static void ApplyEnrichment(MemoryNode memory, KnowledgeUpdate ku, bool isNew)
    {
        if (isNew)
        {
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

        if (ku.Urgency.HasValue)
        {
            memory.Urgency = ku.Urgency.Value;
        }

        if (ku.RelatedEntityIds != null)
        {
            memory.RelatedEntityIds = ku.RelatedEntityIds;
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