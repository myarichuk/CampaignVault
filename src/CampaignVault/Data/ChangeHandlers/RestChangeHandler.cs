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

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var rc = (RestChange)change;

        if (string.IsNullOrWhiteSpace(rc.CharacterId))
        {
            return ChangeHandlerResult.Failure("CharacterId is required.");
        }

        if (!context.Characters.TryGetValue(rc.CharacterId, out var character))
        {
            var suggested = await context.SuggestCharacterMatchAsync(rc.CharacterId);
            return ChangeHandlerResult.Failure($"Character {rc.CharacterId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (string.IsNullOrWhiteSpace(rc.LocationId))
        {
            return ChangeHandlerResult.Failure("LocationId is required.");
        }

        if (!context.Locations.TryGetValue(rc.LocationId, out var location))
        {
            var suggested = await context.SuggestLocationMatchAsync(rc.LocationId);
            return ChangeHandlerResult.Failure($"Location {rc.LocationId} not found." + (suggested != null ? $" Did you mean: {suggested}?" : ""));
        }

        if (rc.IntendedHours <= 0 && rc.RestType != RestType.PerTurn)
        {
            return ChangeHandlerResult.Failure(
                "intendedHours is required and must be > 0 (e.g. 1 for a short rest, 8 for a long rest) — " +
                "it was omitted or 0, which would otherwise silently default to an 8-hour long rest.");
        }

        var time = await context.GetCurrentTimeAsync();

        var (interrupted, hoursRested, deltas, narratives) = await _resolver.EvaluateAsync(
            context,
            character,
            location,
            CalculateRestHours(rc),
            4, // bucket size 4 hours
            rc.SecurityModifier,
            "Rest");

        // Advance time
        if (hoursRested > 0)
        {
            time.AdvanceHours(hoursRested);
        }

        // Dispatch encounter events / transient NPCs
        foreach (var delta in deltas)
        {
            await context.Dispatcher.DispatchMutationAsync(context, delta, ct);
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
                await ClearUntilLongRestConditionsAsync(rc.CharacterId, character, context, ct);
            }

            // Recover eligible resource pools immediately — don't wait for the next advance_world.
            var recoveryNarratives = new List<string>();
            var recoveryDeltas = RestRecoveryLogic.BuildRecoveryDeltas(character, recoveryNarratives);
            foreach (var recoveryDelta in recoveryDeltas)
            {
                await context.Dispatcher.DispatchMutationAsync(context, recoveryDelta, ct);
            }
            foreach (var note in recoveryNarratives)
            {
                context.RecordMessage(note);
            }

            var baseline = context.Config?.NeedSatisfactionBaseline ?? 20;
            var tirednessDelta = RestRecoveryLogic.BuildTirednessRecoveryDelta(character, restType, baseline);
            if (tirednessDelta != null)
            {
                await context.Dispatcher.DispatchMutationAsync(context, tirednessDelta, ct);
                context.RecordMessage($"{character.Name} feels rested ({restType} rest).");
            }

            await context.Dispatcher.DispatchMutationAsync(context, new ActivityChange
            {
                CharacterId = rc.CharacterId,
                NewLocationId = rc.LocationId,
                UpdateLocation = false,
                NewActivity = rc.NarrativeNote ?? "Rested peacefully.",
                Reason = "Rest complete"
            }, ct);

            var recoverySummary = recoveryNarratives.Count > 0
                ? "Resource pools recovered immediately."
                : "No resource pools were eligible to recover.";

            return new ChangeHandlerResult(true,
                $"Rest completed safely. {hoursRested} hours passed ({restType} rest). {recoverySummary}");
        }

        return new ChangeHandlerResult(true, $"Rest INTERRUPTED after {hoursRested} hours! Encounter spawned. Do NOT apply healing commits yet; resolve the encounter first.");

        int CalculateRestHours(RestChange restChange)
        {
            // note: speial case, at the moment used by Fallout system for recharging AP
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
        ChangeContext context,
        CancellationToken ct)
    {
        foreach (var effect in ConditionExpiryEvaluator.CollectLongRestFullClears(character, _conditionProvider))
        {
            await context.Dispatcher.DispatchMutationAsync(context, new StatusRemove
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
            if (!TryParseStackLevel(effect.Name, out var level))
            {
                context.RecordMessage(
                    $"[WARNING] Stacking condition '{effect.Name}' has no parseable numeric level; left unchanged.");
                continue;
            }

            if (level <= 1)
            {
                await context.Dispatcher.DispatchMutationAsync(context, new StatusRemove
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
                var newName = $"{baseName} {level - 1}";

                await context.Dispatcher.DispatchMutationAsync(context, new StatusRemove
                {
                    CharacterId = characterId,
                    Status = effect.Name
                }, ct);

                await context.Dispatcher.DispatchMutationAsync(context, new StatusChange
                {
                    CharacterId = characterId,
                    Effect = CloneStatusEffect(effect, newName)
                }, ct);

                context.RecordMessage(
                    $"Stacking condition decremented to '{newName}' on {characterId} after long rest.");
            }
        }
    }

    private static bool TryParseStackLevel(string name, out int level)
    {
        level = 0;
        var lastSpace = name.LastIndexOf(' ');
        return lastSpace >= 0 && int.TryParse(name[(lastSpace + 1)..], out level);
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