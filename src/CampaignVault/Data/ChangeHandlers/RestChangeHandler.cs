using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

public class RestChangeHandler : IWorldChangeHandler
{
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
            rc.SecurityModifier,
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

            if (restType == RestType.LongRest)
            {
                await ClearUntilLongRestConditionsAsync(rc.CharacterId, character, ctx, ct);
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
        foreach (var effect in ConditionExpiryEvaluator.CollectLongRestDecrements(character, _conditionProvider))
        {
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
            AppliedBy = source.AppliedBy
        };
}