using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

public class RestChangeHandler : IWorldChangeHandler
{
    private const string HitDicePool = "hit_dice";

    private readonly EncounterResolver _resolver;
    private readonly ConditionDefinitionProvider _conditionProvider;

    public RestChangeHandler(EncounterResolver resolver, ConditionDefinitionProvider conditionProvider)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _conditionProvider = conditionProvider ?? throw new ArgumentNullException(nameof(conditionProvider));
    }

    public bool ShouldHandle(WorldChange change) => change is RestChange;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var rc = (RestChange)change;

        if (string.IsNullOrWhiteSpace(rc.CharacterId))
        {
            return ChangeHandlerResult.Failure("CharacterId is required.");
        }

        if (!ctx.Characters.TryGetValue(rc.CharacterId, out var character))
        {
            var suggested = await ctx.SuggestCharacterMatchAsync(rc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {rc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (string.IsNullOrWhiteSpace(rc.LocationId))
        {
            return ChangeHandlerResult.Failure("LocationId is required.");
        }

        if (!ctx.Locations.TryGetValue(rc.LocationId, out var location))
        {
            var suggested = await ctx.SuggestLocationMatchAsync(rc.LocationId);
            return ChangeHandlerResult.Failure($"Location {rc.LocationId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (rc.IntendedHours <= 0 && rc.RestType != RestType.PerTurn)
        {
            return ChangeHandlerResult.Failure(
                "intendedHours is required and must be > 0 (e.g. 1 for a short rest, 8 for a long rest) — " +
                "it was omitted or 0, which would otherwise silently default to an 8-hour long rest.");
        }

        var time = await ctx.GetCurrentTimeAsync();
        location.LastVisitedDay = time.TotalDaysElapsed;
        location.LastUpdated = DateTime.UtcNow;

        var (interrupted, hoursRested, deltas, narratives) = await _resolver.EvaluateAsync(
            ctx,
            character,
            location,
            CalculateRestHours(rc),
            4, // bucket size 4 hours
            -rc.SecurityModifier, // security is safety (+ = safer); the resolver's modifier is risk (+ = more encounters)
            "Rest");

        // Advance time
        if (hoursRested > 0)
        {
            // Hunger/thirst/social_drive still accrue while resting (sleeping doesn't pause metabolism),
            // but they're NOT dispatched here — the day-tick that now reliably fires right after this
            // commit (via CampaignTime.UnsimulatedHours) already accrues those at the ordinary ambient
            // rate for every character, this one included, regardless of whether the rest completes or
            // gets interrupted partway through. Tiredness is the one need still handled specially: it's
            // recovered below (not accrued) on a completed rest, and CampaignRepository.StageChangesAsync
            // marks this character exempt from the ambient tick's tiredness accrual for this commit so
            // resting never simultaneously adds tiredness while the recovery delta removes it.
            time.AdvanceHours(hoursRested);
            ctx.TimeNotes.Add(new TimeAdvancedChange
            {
                Source = "rest",
                Hours = hoursRested,
                BucketHours = 4,
                TotalHoursAfter = time.TotalDaysElapsed * 24.0 + time.Hour,
                CharacterIds = [rc.CharacterId],
                LocationId = rc.LocationId
            });
        }

        // Dispatch encounter events / transient NPCs
        foreach (var delta in deltas)
        {
            await ctx.Dispatcher.DispatchMutationAsync(ctx, delta, ct);
        }

        if (!interrupted)
        {
            // Mark the day when the rest was completed (for spell slot recovery)
            character.LastRestedDay = (int)time.TotalDaysElapsed;

            // Infer or use explicit rest type for pool recovery
            var restType = rc.RestType ?? (hoursRested >= 8 ? RestType.LongRest : RestType.ShortRest);
            character.LastRestType = restType;
            character.RestSequence = (character.RestSequence ?? 0) + 1;

            if (rc.SecurityModifier >= 100 && hoursRested >= 8)
            {
                ctx.RecordMessage(
                    "NOTE: a +100 shelter such as Leomund's Tiny Hut lasts 8 hours. It ends with this rest: narrate the " +
                    "dome as gone, and give any further rest here a normal security modifier.");
            }

            if (hoursRested >= 6 && time.Hour < 5)
            {
                ctx.RecordMessage(
                    $"NOTE: {character.Name}'s rest ended at hour {time.Hour}, still night, not morning. To sleep until " +
                    $"dawn (hour 6) from where it started, intendedHours would have been {hoursRested + (6 - time.Hour)}.");
            }

            if (restType == RestType.LongRest)
            {
                await ClearUntilLongRestConditionsAsync(rc.CharacterId, character, ctx, ct);
                RestoreHitPoints(character, ctx);
                await RecoverHitDiceAsync(character, ctx, ct);
                RecoverPf2eDyingCounters(character, ctx);
                if (character.TempHp is > 0)
                {
                    ctx.RecordMessage($"{character.Name}'s {character.TempHp} temporary HP fade after the long rest.");
                    character.TempHp = null;
                    character.TempHpSource = null;
                }
            }

            // Recover eligible resource pools immediately — don't wait for the next advance_world.
            var recoveryNarratives = new List<string>();
            var recoveryDeltas = RestRecoveryLogic.BuildRecoveryDeltas(character, recoveryNarratives);
            foreach (var recoveryDelta in recoveryDeltas)
            {
                await ctx.Dispatcher.DispatchMutationAsync(ctx, recoveryDelta, ct);
            }
            foreach (var note in recoveryNarratives)
            {
                ctx.RecordMessage(note);
            }

            var baseline = ctx.Config?.NeedSatisfactionBaseline ?? 20;
            var tirednessDelta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, restType, baseline);
            if (tirednessDelta != null)
            {
                await ctx.Dispatcher.DispatchMutationAsync(ctx, tirednessDelta, ct);
                ctx.RecordMessage($"{character.Name} feels rested ({restType} rest).");
            }

            await ctx.Dispatcher.DispatchMutationAsync(ctx, new ActivityChange
            {
                CharacterId = rc.CharacterId,
                UpdateLocation = false,
                NewActivity = rc.NarrativeNote ?? "Rested peacefully.",
                Reason = "Rest complete"
            }, ct);

            ctx.Publish(CoreEvents.Rested, new Dictionary<string, object?>
            {
                [CoreEvents.Fields.CharacterId] = character.Id,
                [CoreEvents.Fields.LocationId] = location.Id,
                [CoreEvents.Fields.RestType] = restType.ToString(),
                [CoreEvents.Fields.Hours] = hoursRested
            });

            var recoverySummary = recoveryNarratives.Count > 0
                ? "Resource pools recovered immediately."
                : "No resource pools were eligible to recover.";
            if (restType == RestType.LongRest)
            {
                recoverySummary += $" Hit points {character.CurrentHp}/{character.MaxHp}.";
            }
            else if (restType == RestType.ShortRest && HasHitDice(character))
            {
                recoverySummary += " A short rest heals nothing by itself: to spend hit dice, commit a resource change on hit_dice (delta -1 each) " +
                    "plus an HP change of the die roll and the Constitution modifier.";
            }

            return new ChangeHandlerResult(true,
                $"Rest completed safely. {hoursRested} hours passed ({restType} rest). {recoverySummary}");
        }

        return new ChangeHandlerResult(true, $"Rest INTERRUPTED after {hoursRested} hours! Encounter spawned. Do NOT apply healing commits yet; resolve the encounter first.");

        int CalculateRestHours(RestChange restChange)
        {
            // PerTurn rests (e.g. per-round resource recharges) don't advance in-world time.
            if (restChange.RestType == RestType.PerTurn)
            {
                return 0;
            }

            return restChange.IntendedHours;
        }
    }

    /// <summary>
    /// A long rest heals. 5e: to the hit point maximum in force (exhaustion 4 lowers it). PF2e: a night's rest gives back the
    /// Constitution modifier (at least 1) times the level, not everything (Player Core, Rest). A character who is dying, or dead,
    /// is not healed: the dying rules decide that first.
    /// </summary>
    private void RestoreHitPoints(Character character, ChangeContext ctx)
    {
        if (character.MaxHp <= 0 || character.IsDead)
        {
            return;
        }

        if (HitPointState.IsDying(character))
        {
            ctx.RecordMessage($"{character.Name} is dying: a rest heals nothing until they are stabilized or healed.");
            return;
        }

        var cap = ConditionMechanics.EffectiveMaxHp(character, _conditionProvider);
        var target = character.SystemStats is Pf2eExtension pf2e
            ? character.CurrentHp + Math.Max(1, pf2e.ConstitutionMod) * Math.Max(1, pf2e.Level ?? 1)
            : cap;
        if (character.CurrentHp >= Math.Min(target, cap))
        {
            return;
        }

        var before = character.CurrentHp;
        var transition = HitPointState.Set(character, target, cap);
        ctx.RecordMessage($"{character.Name} regains {character.CurrentHp - before} hit points from the long rest (now {character.CurrentHp}/{character.MaxHp}).");
        if (transition.Wounded is { } wounded)
        {
            ctx.RecordMessage($"{character.Name} is conscious again; dying ends and they are now wounded {wounded}.");
        }
    }

    private static bool HasHitDice(Character character) =>
        character.SystemStats is Dnd5eExtension && character.SystemStats.ResourcePools?.ContainsKey(HitDicePool) == true;

    /// <summary>5e: a long rest regains half the total hit dice, at least one. The pool has no recovery of its own, so this is the only place they come back.</summary>
    private async Task RecoverHitDiceAsync(Character character, ChangeContext ctx, CancellationToken ct)
    {
        if (!HasHitDice(character) || character.IsDead)
        {
            return;
        }

        var pool = character.SystemStats!.ResourcePools![HitDicePool];
        var regain = Math.Min(pool.Max - pool.Current, Math.Max(1, pool.Max / 2));
        if (regain <= 0)
        {
            return;
        }

        await ctx.Dispatcher.DispatchMutationAsync(ctx, new ResourceChange
        {
            CharacterId = character.Id,
            PoolName = HitDicePool,
            Delta = regain,
            Reason = "Long rest: half the hit dice (at least one)"
        }, ct);
        ctx.RecordMessage($"{character.Name} regains {regain} hit {(regain == 1 ? "die" : "dice")} from the long rest.");
    }

    /// <summary>The 5e long rest reduces exhaustion only when the character has also eaten and drunk (SRD, Resting). Lacking them means starving or parched, the same severe line the deprivation rule uses (a character who is merely peckish has had some). The needs are read before the rest's own hours accrue.</summary>
    private static string? MissingSustenance(Character character)
    {
        var needs = character.Needs?.ActiveNeeds;
        if (needs is null || character.SystemStats is not Dnd5eExtension)
        {
            return null;
        }

        var hungry = needs.GetValueOrDefault("hunger") >= SurvivalThresholds.SevereHunger;
        var thirsty = needs.GetValueOrDefault("thirst") >= SurvivalThresholds.SevereThirst;
        return (hungry, thirsty) switch
        {
            (true, true) => "food or drink",
            (true, false) => "food",
            (false, true) => "drink",
            _ => null,
        };
    }

    /// <summary>
    /// PF2e: a character back at full hit points loses wounded after resting (Player Core: wounded ends when fully healed and
    /// rested 10 minutes). The rest heals first, so wounded ends only for one who is back at maximum. Doomed is a stacking
    /// until-long-rest condition, so the generic long-rest decrement already lowers it.
    /// </summary>
    private static void RecoverPf2eDyingCounters(Character character, ChangeContext ctx)
    {
        if (!Pf2eDying.Applies(character))
        {
            return;
        }

        if (Pf2eDying.Get(character, Pf2eDying.Wounded) > 0 && character.CurrentHp >= character.MaxHp)
        {
            Pf2eDying.Set(character, Pf2eDying.Wounded, 0);
            ctx.RecordMessage($"{character.Name} is at full hit points and rested: wounded ends.");
        }
    }

    private async Task ClearUntilLongRestConditionsAsync(
        string characterId,
        Character character,
        IChangeContext context,
        CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        foreach (var effect in ConditionExpiryEvaluator.CollectLongRestFullClears(character, _conditionProvider))
        {
            await ctx.Dispatcher.DispatchMutationAsync(ctx, new StatusRemove
            {
                CharacterId = characterId,
                Status = effect.Name
            }, ct);
            context.RecordMessage(
                $"UntilLongRest condition '{effect.Name}' cleared on {characterId} after long rest.");
        }

        // Stacking conditions (e.g. dnd5e exhaustion) decrement by one level per long rest
        // instead of fully clearing — see ConditionDefinition.IsStacking.
        var missing = MissingSustenance(character);
        foreach (var effect in ConditionExpiryEvaluator.CollectLongRestDecrements(character, _conditionProvider))
        {
            if (missing is not null)
            {
                context.RecordMessage(
                    $"'{effect.Name}' on {character.Name} stays: a long rest only lowers it when they have also had {missing}. " +
                    "Feed or water them, then rest again.");
                continue;
            }

            if (!ConditionExpiryEvaluator.TryParseStackLevel(effect.Name, out var level))
            {
                context.RecordMessage(
                    $"[WARNING] Stacking condition '{effect.Name}' has no parseable numeric level; left unchanged.");
                continue;
            }

            if (level <= 1)
            {
                await ctx.Dispatcher.DispatchMutationAsync(ctx, new StatusRemove
                {
                    CharacterId = characterId,
                    Status = effect.Name
                }, ct);
                context.RecordMessage(
                    $"Stacking condition '{effect.Name}' reached 0 and was cleared on {characterId} after long rest.");
            }
            else
            {
                var baseName = effect.Name[..effect.Name.LastIndexOf(' ')];
                var newName = ConditionExpiryEvaluator.FormatStackLevel(baseName, level - 1);

                await ctx.Dispatcher.DispatchMutationAsync(ctx, new StatusRemove
                {
                    CharacterId = characterId,
                    Status = effect.Name
                }, ct);

                await ctx.Dispatcher.DispatchMutationAsync(ctx, new StatusChange
                {
                    CharacterId = characterId,
                    Effect = CloneStatusEffect(effect, newName)
                }, ct);

                context.RecordMessage(
                    $"Stacking condition decremented to '{newName}' on {characterId} after long rest.");
            }
        }
    }

    private static StatusEffect CloneStatusEffect(StatusEffect source, string newName) =>
        new()
        {
            Name = newName,
            Category = source.Category,
            ConditionName = source.ConditionName,
            AffectedPart = source.AffectedPart,
            StatModifiers = new Dictionary<string, float>(source.StatModifiers),
            ExpiresAtDay = source.ExpiresAtDay,
            ExpiresAtRound = source.ExpiresAtRound,
            RecoveryHint = source.RecoveryHint,
            AppliedBy = source.AppliedBy,
            EffectKey = source.EffectKey,
            EffectTier = source.EffectTier,
            ExpiresAtOwnTurnStart = source.ExpiresAtOwnTurnStart,
            PendingDamage = source.PendingDamage
        };
}